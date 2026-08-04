using System;
using System.Collections.Generic;
using System.Linq;
using ERP.Domain.Common;
using ERP.SharedKernel;

namespace ERP.Domain.Accounting;

public enum JournalEntryStatus
{
    Draft = 1,
    Posted = 2,
    Reversed = 3
}

/// <summary>Identifies which module/document produced this entry, enabling drill-down
/// from the General Ledger back to the originating Sales Invoice, PO, Payroll run, etc.
/// This traceability is what separates a real accounting engine from a spreadsheet.</summary>
public enum JournalSourceModule
{
    Manual = 0,
    Sales = 1,
    Purchasing = 2,
    Inventory = 3,
    FixedAssets = 4,
    Payroll = 5,
    Manufacturing = 6
}

/// <summary>
/// AGGREGATE ROOT of the entire Financial Accounting module and, transitively, of
/// the whole ERP's financial integrity. Every economic event in every other module
/// (Sales, Purchasing, Inventory, Payroll, Fixed Assets) ultimately materializes as
/// one JournalEntry here.
///
/// CORE INVARIANT (enforced, never bypassable): total debits MUST equal total
/// credits at all times a JournalEntry transitions to Posted. This is not a
/// validation rule bolted on in the Application layer — it is a domain invariant
/// enforced inside the aggregate itself, because allowing an unbalanced entry to
/// ever reach the database would corrupt the ledger irrecoverably.
/// </summary>
public class JournalEntry : AggregateRoot
{
    public string EntryNumber { get; private set; } = default!;   // sequential, company-scoped, e.g. "JE-2026-000123"
    public DateTime PostingDate { get; private set; }
    public Guid FiscalYearId { get; private set; }
    public Guid AccountingPeriodId { get; private set; }
    public Guid CompanyId { get; private set; }
    public Guid BranchId { get; private set; }
    public string Description { get; private set; } = default!;
    public JournalEntryStatus Status { get; private set; } = JournalEntryStatus.Draft;
    public JournalSourceModule SourceModule { get; private set; }
    public string? SourceDocumentNumber { get; private set; } // e.g. the Sales Invoice number that generated this entry
    public Guid? SourceDocumentId { get; private set; }
    public Guid? ReversalOfEntryId { get; private set; }       // set when this entry reverses another

    private readonly List<JournalLine> _lines = new();
    public IReadOnlyCollection<JournalLine> Lines => _lines.AsReadOnly();

    public decimal TotalDebit => _lines.Sum(l => l.Debit);
    public decimal TotalCredit => _lines.Sum(l => l.Credit);
    public bool IsBalanced => TotalDebit == TotalCredit && TotalDebit > 0;

    private JournalEntry() { } // EF Core

    /// <summary>
    /// Factory method — the ONLY way to construct a JournalEntry. Starts in Draft
    /// with no lines; lines are added via <see cref="AddLine"/>, and the entry can
    /// only transition to Posted through <see cref="Post"/>, which re-validates
    /// balance. This funnel prevents any code path from producing a malformed entry.
    /// </summary>
    public static JournalEntry CreateDraft(
        string entryNumber, DateTime postingDate, Guid fiscalYearId, Guid accountingPeriodId,
        Guid companyId, Guid branchId, string description,
        JournalSourceModule sourceModule = JournalSourceModule.Manual,
        string? sourceDocumentNumber = null, Guid? sourceDocumentId = null)
    {
        return new JournalEntry
        {
            EntryNumber = entryNumber,
            PostingDate = postingDate,
            FiscalYearId = fiscalYearId,
            AccountingPeriodId = accountingPeriodId,
            CompanyId = companyId,
            BranchId = branchId,
            Description = description,
            SourceModule = sourceModule,
            SourceDocumentNumber = sourceDocumentNumber,
            SourceDocumentId = sourceDocumentId
        };
    }

    /// <summary>Adds one debit-or-credit line. A line must be exactly one-sided —
    /// a real accounting system never allows a single line to carry both a debit
    /// and a credit amount simultaneously.</summary>
    public Result AddLine(Guid accountId, decimal debit, decimal credit, string currencyCode,
        Guid? costCenterId = null, string? lineDescription = null)
    {
        if (Status != JournalEntryStatus.Draft)
            return Result.Failure("Cannot add lines to a journal entry that is not in Draft status.");

        if (debit < 0 || credit < 0)
            return Result.Failure("Debit and credit amounts cannot be negative.");
        if (debit > 0 && credit > 0)
            return Result.Failure("A journal line cannot have both a debit and a credit amount.");
        if (debit == 0 && credit == 0)
            return Result.Failure("A journal line must have either a debit or a credit amount greater than zero.");

        var moneyResult = Money.Create(debit > 0 ? debit : credit, currencyCode);
        if (moneyResult.IsFailure)
            return Result.Failure(moneyResult.Errors);

        _lines.Add(JournalLine.Create(Id, accountId, debit, credit, currencyCode, costCenterId, lineDescription));
        return Result.Success();
    }

    /// <summary>
    /// Posts the entry: validates the double-entry invariant, the period is open,
    /// and every referenced account is a valid leaf/postable account (that last
    /// check happens in the domain service <see cref="Services.JournalPostingService"/>
    /// which has access to the Account repository — this aggregate alone cannot see
    /// other aggregates, per DDD aggregate-boundary rules). On success, raises
    /// <see cref="JournalEntryPostedEvent"/> so the GL projection / read model can
    /// update asynchronously.
    /// </summary>
    public Result Post(AccountingPeriod period)
    {
        if (Status != JournalEntryStatus.Draft)
            return Result.Failure("Only draft journal entries can be posted.");

        if (_lines.Count < 2)
            return Result.Failure("A journal entry must have at least two lines (one debit, one credit).");

        if (!IsBalanced)
            return Result.Failure(
                $"Journal entry is not balanced: total debit {TotalDebit:N2} does not equal total credit {TotalCredit:N2}. " +
                "A journal entry cannot be posted unless debits equal credits.");

        var periodCheck = period.EnsureOpenForPosting();
        if (periodCheck.IsFailure)
            return periodCheck;

        Status = JournalEntryStatus.Posted;
        AddDomainEvent(new JournalEntryPostedEvent(Id, EntryNumber, PostingDate, CompanyId, TotalDebit));
        return Result.Success();
    }

    /// <summary>
    /// Creates a fully mirrored reversing entry (debits become credits and vice
    /// versa) for the given date. Used for period-end accruals, error correction,
    /// or cancelling a posted source document (e.g. a cancelled Sales Invoice) —
    /// never delete a posted entry; always reverse it, preserving the audit trail.
    /// </summary>
    public Result<JournalEntry> CreateReversal(string reversalEntryNumber, DateTime reversalDate,
        Guid accountingPeriodId, string reason)
    {
        if (Status != JournalEntryStatus.Posted)
            return Result.Failure<JournalEntry>("Only posted journal entries can be reversed.");

        var reversal = CreateDraft(reversalEntryNumber, reversalDate, FiscalYearId, accountingPeriodId,
            CompanyId, BranchId, $"Reversal of {EntryNumber}: {reason}", SourceModule, SourceDocumentNumber, SourceDocumentId);
        reversal.ReversalOfEntryId = Id;

        foreach (var line in _lines)
        {
            // swap debit/credit to reverse the economic effect
            reversal.AddLine(line.AccountId, line.Credit, line.Debit, line.CurrencyCode, line.CostCenterId, line.Description);
        }

        Status = JournalEntryStatus.Reversed;
        return Result.Success(reversal);
    }
}

/// <summary>
/// Child entity of JournalEntry (not an aggregate root — never accessed outside
/// its parent JournalEntry). Represents a single debit or credit line.
/// </summary>
public class JournalLine : BaseEntity
{
    public Guid JournalEntryId { get; private set; }
    public Guid AccountId { get; private set; }
    public decimal Debit { get; private set; }
    public decimal Credit { get; private set; }
    public string CurrencyCode { get; private set; } = default!;
    public Guid? CostCenterId { get; private set; }
    public string? Description { get; private set; }

    private JournalLine() { }

    internal static JournalLine Create(Guid journalEntryId, Guid accountId, decimal debit, decimal credit,
        string currencyCode, Guid? costCenterId, string? description) => new()
    {
        JournalEntryId = journalEntryId,
        AccountId = accountId,
        Debit = debit,
        Credit = credit,
        CurrencyCode = currencyCode,
        CostCenterId = costCenterId,
        Description = description
    };
}

/// <summary>Raised when a JournalEntry successfully posts. Consumed by: (1) the GL
/// balance read-model projector, (2) SignalR hub to push a live dashboard KPI
/// update, (3) Hangfire job that recalculates dependent reports (Trial Balance
/// cache invalidation).</summary>
public sealed record JournalEntryPostedEvent(
    Guid JournalEntryId, string EntryNumber, DateTime PostingDate, Guid CompanyId, decimal TotalAmount)
    : IDomainEvent
{
    public DateTime OccurredOn { get; } = DateTime.UtcNow;
}
