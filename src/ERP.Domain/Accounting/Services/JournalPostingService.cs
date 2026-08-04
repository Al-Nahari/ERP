using System;
using System.Threading;
using System.Threading.Tasks;
using ERP.SharedKernel;

namespace ERP.Domain.Accounting.Services;

/// <summary>
/// Repository contract for the Account aggregate (implemented in Persistence).
/// </summary>
public interface IAccountRepository : IRepository<Account>
{
    Task<Account?> GetByCodeAsync(string code, Guid companyId, CancellationToken ct = default);
}

public interface IFiscalYearRepository : IRepository<FiscalYear>
{
    Task<FiscalYear?> GetContainingDateAsync(DateTime date, Guid companyId, CancellationToken ct = default);
}

public interface IJournalEntryRepository : IRepository<JournalEntry>
{
    Task<string> GenerateNextEntryNumberAsync(Guid companyId, int fiscalYear, CancellationToken ct = default);
}

/// <summary>
/// Domain Service (per DDD: operations that don't naturally belong to a single
/// aggregate because they coordinate several — here, JournalEntry + Account +
/// FiscalYear). This is the single choke point every other module's posting logic
/// (Sales, Purchasing, Payroll, Inventory, Fixed Assets) calls through, which is
/// what guarantees the whole ERP's ledger stays internally consistent: no module
/// is allowed to write a JournalEntry any other way.
///
/// Uses the STRATEGY pattern via <see cref="IPostingRule"/> — each source module
/// supplies its own rule object describing which accounts to debit/credit for its
/// document type (e.g. SalesInvoicePostingRule, PurchaseInvoicePostingRule,
/// PayrollPostingRule), while this service owns the invariant enforcement
/// (balance, period-open, account-postable) that is common to all of them.
/// </summary>
public class JournalPostingService
{
    private readonly IAccountRepository _accountRepository;
    private readonly IFiscalYearRepository _fiscalYearRepository;
    private readonly IJournalEntryRepository _journalEntryRepository;

    public JournalPostingService(
        IAccountRepository accountRepository,
        IFiscalYearRepository fiscalYearRepository,
        IJournalEntryRepository journalEntryRepository)
    {
        _accountRepository = accountRepository;
        _fiscalYearRepository = fiscalYearRepository;
        _journalEntryRepository = journalEntryRepository;
    }

    /// <summary>
    /// Builds, validates, and posts a journal entry from a set of posting lines
    /// supplied by any module. This is the method a Sales Invoice, Purchase
    /// Invoice, Payroll run, or Depreciation job ultimately calls.
    /// </summary>
    public async Task<Result<JournalEntry>> PostAsync(
        Guid companyId, Guid branchId, DateTime postingDate, string description,
        JournalSourceModule sourceModule, string? sourceDocumentNumber, Guid? sourceDocumentId,
        (Guid accountId, decimal debit, decimal credit, Guid? costCenterId)[] postingLines,
        string currencyCode, CancellationToken ct = default)
    {
        var fiscalYear = await _fiscalYearRepository.GetContainingDateAsync(postingDate, companyId, ct);
        if (fiscalYear is null)
            return Result.Failure<JournalEntry>($"No fiscal year is configured covering the date {postingDate:yyyy-MM-dd}.");

        var periodResult = fiscalYear.GetPeriodForDate(postingDate);
        if (periodResult.IsFailure)
            return Result.Failure<JournalEntry>(periodResult.Errors);

        var entryNumber = await _journalEntryRepository.GenerateNextEntryNumberAsync(companyId, postingDate.Year, ct);

        var entry = JournalEntry.CreateDraft(entryNumber, postingDate, fiscalYear.Id, periodResult.Value.Id,
            companyId, branchId, description, sourceModule, sourceDocumentNumber, sourceDocumentId);

        foreach (var line in postingLines)
        {
            var account = await _accountRepository.GetByIdAsync(line.accountId, ct);
            if (account is null)
                return Result.Failure<JournalEntry>($"Account {line.accountId} was not found.");

            var postableCheck = account.CanBePosted();
            if (postableCheck.IsFailure)
                return Result.Failure<JournalEntry>(postableCheck.Errors);

            if (account.RequiresCostCenter && line.costCenterId is null)
                return Result.Failure<JournalEntry>($"Account '{account.Code}' requires a cost center to be specified.");

            var addResult = entry.AddLine(line.accountId, line.debit, line.credit, currencyCode, line.costCenterId);
            if (addResult.IsFailure)
                return Result.Failure<JournalEntry>(addResult.Errors);
        }

        var postResult = entry.Post(periodResult.Value);
        if (postResult.IsFailure)
            return Result.Failure<JournalEntry>(postResult.Errors);

        await _journalEntryRepository.AddAsync(entry, ct);
        return Result.Success(entry);
    }
}

/// <summary>
/// Strategy contract implemented once per source-document type. Each module
/// (Sales, Purchasing, Payroll...) implements this to declare "given this document,
/// which accounts get debited/credited and by how much" without knowing anything
/// about period control, balance validation, or persistence — that's the
/// JournalPostingService's job. This is the Strategy pattern applied to the classic
/// ERP problem of "every document type has different posting rules."
/// </summary>
public interface IPostingRule<TDocument>
{
    (Guid accountId, decimal debit, decimal credit, Guid? costCenterId)[] BuildPostingLines(TDocument document);
    JournalSourceModule SourceModule { get; }
}
