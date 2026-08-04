using System;
using System.Threading;
using System.Threading.Tasks;
using ERP.Domain.Accounting;
using ERP.Domain.Accounting.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Persistence.Repositories.Accounting;

public class AccountRepository : Repository<Account>, IAccountRepository
{
    public AccountRepository(AppDbContext context) : base(context) { }

    public async Task<Account?> GetByCodeAsync(string code, Guid companyId, CancellationToken ct = default) =>
        await Context.Accounts.FirstOrDefaultAsync(a => a.Code == code && a.CompanyId == companyId, ct);
}

public class FiscalYearRepository : Repository<FiscalYear>, IFiscalYearRepository
{
    public FiscalYearRepository(AppDbContext context) : base(context) { }

    // Periods is an EF Core "owned collection" (see FiscalYearConfiguration.OwnsMany)
    // and is therefore always loaded automatically with its owner — no .Include()
    // needed, and EF Core disallows explicitly Include-ing owned navigations.
    public async Task<FiscalYear?> GetContainingDateAsync(DateTime date, Guid companyId, CancellationToken ct = default) =>
        await Context.FiscalYears
            .FirstOrDefaultAsync(f => f.CompanyId == companyId && date >= f.StartDate && date <= f.EndDate, ct);
}

public class JournalEntryRepository : Repository<JournalEntry>, IJournalEntryRepository
{
    public JournalEntryRepository(AppDbContext context) : base(context) { }

    /// <summary>
    /// Generates the next sequential, gap-free-per-year entry number
    /// (e.g. "JE-2026-000123") scoped to CompanyId + calendar year. Uses a raw
    /// atomic UPDATE...OUTPUT against a dedicated NumberSequences table (created
    /// in a migration) rather than MAX(EntryNumber)+1, because MAX-based
    /// numbering race-conditions under concurrent posting — exactly the kind of
    /// bug that silently corrupts audit trails in cheaper ERP implementations.
    /// </summary>
    public async Task<string> GenerateNextEntryNumberAsync(Guid companyId, int fiscalYear, CancellationToken ct = default)
    {
        var sequenceKey = $"JE-{companyId}-{fiscalYear}";

        // Executes: MERGE-based upsert-and-increment in one round trip, returning
        // the new value — avoids the read-then-write race condition of
        // SELECT MAX(..) + 1 under concurrent journal postings.
        var nextValue = await Context.Database.SqlQuery<int>(
                $@"MERGE INTO Accounting.NumberSequences AS target
                   USING (SELECT {sequenceKey} AS SequenceKey) AS src
                   ON target.SequenceKey = src.SequenceKey
                   WHEN MATCHED THEN UPDATE SET target.CurrentValue = target.CurrentValue + 1
                   WHEN NOT MATCHED THEN INSERT (SequenceKey, CurrentValue) VALUES (src.SequenceKey, 1)
                   OUTPUT inserted.CurrentValue AS Value;")
            .SingleAsync(ct);

        return $"JE-{fiscalYear}-{nextValue:D6}";
    }
}

public static class AccountingRepositoryServiceCollectionExtensions
{
    public static IServiceCollection AddAccountingRepositories(this IServiceCollection services) => services
        .AddScoped<IAccountRepository, AccountRepository>()
        .AddScoped<IFiscalYearRepository, FiscalYearRepository>()
        .AddScoped<IJournalEntryRepository, JournalEntryRepository>()
        .AddScoped<JournalPostingService>();
}
