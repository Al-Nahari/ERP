using ERP.Domain.Accounting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ERP.Persistence.Configurations.Accounting;

public class AccountGroupConfiguration : IEntityTypeConfiguration<AccountGroup>
{
    public void Configure(EntityTypeBuilder<AccountGroup> builder)
    {
        builder.ToTable("AccountGroups", schema: "Accounting");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NameAr).HasMaxLength(200).IsRequired();

        // Company + Code must be unique — same code cannot be reused within a company's
        // chart of accounts, but different companies (multi-company) can reuse codes.
        builder.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();

        builder.HasOne<AccountGroup>().WithMany().HasForeignKey(x => x.ParentGroupId).OnDelete(DeleteBehavior.Restrict);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class AccountConfiguration : IEntityTypeConfiguration<Account>
{
    public void Configure(EntityTypeBuilder<Account> builder)
    {
        builder.ToTable("Accounts", schema: "Accounting");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.Property(x => x.NameEn).HasMaxLength(200).IsRequired();
        builder.Property(x => x.NameAr).HasMaxLength(200).IsRequired();
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();

        builder.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        // Speeds up the hottest query in the whole system: "find account by code
        // for company X" during every single posting operation.
        builder.HasIndex(x => new { x.CompanyId, x.IsActive });

        builder.HasOne<AccountGroup>().WithMany().HasForeignKey(x => x.AccountGroupId).OnDelete(DeleteBehavior.Restrict);

        builder.Property(x => x.RowVersion).IsRowVersion();
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class CostCenterConfiguration : IEntityTypeConfiguration<CostCenter>
{
    public void Configure(EntityTypeBuilder<CostCenter> builder)
    {
        builder.ToTable("CostCenters", schema: "Accounting");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Code).HasMaxLength(20).IsRequired();
        builder.HasIndex(x => new { x.CompanyId, x.Code }).IsUnique();
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class FiscalYearConfiguration : IEntityTypeConfiguration<FiscalYear>
{
    public void Configure(EntityTypeBuilder<FiscalYear> builder)
    {
        builder.ToTable("FiscalYears", schema: "Accounting");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Name).HasMaxLength(20).IsRequired();
        builder.HasIndex(x => new { x.CompanyId, x.StartDate, x.EndDate });

        // Periods are owned — they cannot exist without a parent FiscalYear and
        // are always loaded/saved together with it (true DDD aggregate boundary).
        builder.OwnsMany(x => x.Periods, pb =>
        {
            pb.ToTable("AccountingPeriods", schema: "Accounting");
            pb.WithOwner().HasForeignKey("FiscalYearId");
            pb.HasKey(nameof(AccountingPeriod.Id));
            pb.Property<System.Guid>(nameof(AccountingPeriod.Id)).ValueGeneratedNever();
            pb.HasIndex("FiscalYearId", nameof(AccountingPeriod.SequenceNumber)).IsUnique();
        });

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class JournalEntryConfiguration : IEntityTypeConfiguration<JournalEntry>
{
    public void Configure(EntityTypeBuilder<JournalEntry> builder)
    {
        builder.ToTable("JournalEntries", schema: "Accounting");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.EntryNumber).HasMaxLength(30).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500).IsRequired();
        builder.Property(x => x.SourceDocumentNumber).HasMaxLength(50);

        builder.HasIndex(x => new { x.CompanyId, x.EntryNumber }).IsUnique();
        // Critical for reporting performance: Trial Balance / GL queries always
        // filter by company + posting date range + status=Posted.
        builder.HasIndex(x => new { x.CompanyId, x.PostingDate, x.Status });
        builder.HasIndex(x => new { x.SourceModule, x.SourceDocumentId }); // drill-down from source doc

        builder.HasOne<FiscalYear>().WithMany().HasForeignKey(x => x.FiscalYearId).OnDelete(DeleteBehavior.Restrict);

        // Lines are a true child collection of the aggregate root — configured as
        // a regular dependent table (not Owned, since JournalLine has its own Id
        // and audit columns) but ALWAYS loaded/saved through JournalEntry, never
        // queried directly, enforcing the aggregate boundary at the repository level.
        builder.HasMany(x => x.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).OnDelete(DeleteBehavior.Cascade);

        builder.Ignore(x => x.DomainEvents);
        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}

public class JournalLineConfiguration : IEntityTypeConfiguration<JournalLine>
{
    public void Configure(EntityTypeBuilder<JournalLine> builder)
    {
        builder.ToTable("JournalLines", schema: "Accounting");
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Debit).HasColumnType("decimal(18,2)");
        builder.Property(x => x.Credit).HasColumnType("decimal(18,2)");
        builder.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();

        builder.HasOne<Account>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CostCenter>().WithMany().HasForeignKey(x => x.CostCenterId).OnDelete(DeleteBehavior.Restrict);

        // Speeds up "all lines posted against account X" — the basis of the
        // General Ledger drill-down report and account balance calculation.
        builder.HasIndex(x => x.AccountId);

        builder.HasQueryFilter(x => !x.IsDeleted);
    }
}
