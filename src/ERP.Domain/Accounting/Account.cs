using System;
using System.Collections.Generic;
using ERP.Domain.Common;
using ERP.SharedKernel;

namespace ERP.Domain.Accounting;

/// <summary>Nature of an account balance — determines whether a debit or a credit
/// increases it. This single distinction is what makes the double-entry engine
/// self-consistent: every posting rule reduces to "does this debit/credit increase
/// or decrease the account's Nature".</summary>
public enum AccountNature
{
    Debit = 1,   // Assets, Expenses — increased by debit
    Credit = 2   // Liabilities, Equity, Revenue — increased by credit
}

public enum AccountClassification
{
    Asset = 1,
    Liability = 2,
    Equity = 3,
    Revenue = 4,
    Expense = 5
}

/// <summary>
/// Chart-of-Accounts grouping node (e.g. "Current Assets", "Operating Expenses").
/// Forms a self-referencing tree so the Trial Balance / Balance Sheet can be
/// rendered hierarchically per company policy.
/// </summary>
public class AccountGroup : AggregateRoot
{
    public string Code { get; private set; } = default!;
    public string NameEn { get; private set; } = default!;
    public string NameAr { get; private set; } = default!;
    public AccountClassification Classification { get; private set; }
    public Guid? ParentGroupId { get; private set; }
    public Guid CompanyId { get; private set; }

    private AccountGroup() { } // EF Core

    public static Result<AccountGroup> Create(string code, string nameEn, string nameAr,
        AccountClassification classification, Guid companyId, Guid? parentGroupId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<AccountGroup>("Account group code is required.");

        var group = new AccountGroup
        {
            Code = code,
            NameEn = nameEn,
            NameAr = nameAr,
            Classification = classification,
            CompanyId = companyId,
            ParentGroupId = parentGroupId
        };
        return Result.Success(group);
    }
}

/// <summary>
/// Aggregate Root of the Chart of Accounts. Every financial posting in the entire
/// ERP ultimately resolves to a debit/credit against an <see cref="Account"/>.
/// The account enforces its own invariants: it cannot be posted to if it is a
/// summary/header account (only leaf accounts accept postings), and it cannot be
/// deactivated while it carries a non-zero balance concept is enforced at the
/// application-service level via the ledger, not here (this aggregate only knows
/// its own definition, not its running balance — that is a GL projection).
/// </summary>
public class Account : AggregateRoot
{
    public string Code { get; private set; } = default!;       // e.g. "1101"
    public string NameEn { get; private set; } = default!;
    public string NameAr { get; private set; } = default!;
    public AccountNature Nature { get; private set; }
    public AccountClassification Classification { get; private set; }
    public Guid AccountGroupId { get; private set; }
    public Guid CompanyId { get; private set; }
    public string CurrencyCode { get; private set; } = "SAR";

    /// <summary>Header accounts are structural only (e.g. "1000 - Assets") and
    /// cannot receive journal postings directly — only their leaf descendants can.
    /// This mirrors how SAP B1 / Dynamics BC prevent posting to summary nodes.</summary>
    public bool IsHeaderAccount { get; private set; }

    public bool IsActive { get; private set; } = true;
    public bool RequiresCostCenter { get; private set; }

    private Account() { }

    public static Result<Account> Create(
        string code, string nameEn, string nameAr, AccountNature nature,
        AccountClassification classification, Guid accountGroupId, Guid companyId,
        bool isHeaderAccount = false, string currencyCode = "SAR", bool requiresCostCenter = false)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<Account>("Account code is required.");
        if (string.IsNullOrWhiteSpace(nameEn) || string.IsNullOrWhiteSpace(nameAr))
            return Result.Failure<Account>("Account name (both EN and AR) is required.");

        return Result.Success(new Account
        {
            Code = code,
            NameEn = nameEn,
            NameAr = nameAr,
            Nature = nature,
            Classification = classification,
            AccountGroupId = accountGroupId,
            CompanyId = companyId,
            IsHeaderAccount = isHeaderAccount,
            CurrencyCode = currencyCode,
            RequiresCostCenter = requiresCostCenter
        });
    }

    /// <summary>
    /// Signed effect of a raw debit/credit pair on this account's natural balance.
    /// Positive result increases the account, negative decreases it. Centralizing
    /// this here means the GL balance calculator, Trial Balance, and Balance Sheet
    /// reports all derive from one authoritative rule instead of duplicating
    /// "if debit account then debit-credit else credit-debit" logic everywhere.
    /// </summary>
    public decimal SignedEffect(decimal debit, decimal credit) =>
        Nature == AccountNature.Debit ? debit - credit : credit - debit;

    public Result CanBePosted()
    {
        if (IsHeaderAccount)
            return Result.Failure($"Account '{Code}' is a header account and cannot receive direct postings.");
        if (!IsActive)
            return Result.Failure($"Account '{Code}' is inactive.");
        return Result.Success();
    }

    public void Deactivate() => IsActive = false;
    public void Activate() => IsActive = true;
}

/// <summary>Cost Center — enables dimensional accounting (profit/cost tracking per
/// department, project, or branch) orthogonal to the Chart of Accounts, as required
/// for management reporting in any real ERP.</summary>
public class CostCenter : AggregateRoot
{
    public string Code { get; private set; } = default!;
    public string NameEn { get; private set; } = default!;
    public string NameAr { get; private set; } = default!;
    public Guid CompanyId { get; private set; }
    public Guid? ParentCostCenterId { get; private set; }
    public bool IsActive { get; private set; } = true;

    private CostCenter() { }

    public static Result<CostCenter> Create(string code, string nameEn, string nameAr, Guid companyId, Guid? parentId = null)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Result.Failure<CostCenter>("Cost center code is required.");

        return Result.Success(new CostCenter
        {
            Code = code, NameEn = nameEn, NameAr = nameAr, CompanyId = companyId, ParentCostCenterId = parentId
        });
    }
}
