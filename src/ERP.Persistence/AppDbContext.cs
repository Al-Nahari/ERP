using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP.Application.Accounting.Queries.GetTrialBalance;
using ERP.Domain.Accounting;
using ERP.SharedKernel;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP.Persistence;

/// <summary>
/// The single EF Core write-side DbContext for the whole ERP. Modules are kept
/// logically separate via SQL schemas ("Accounting", "Inventory", "Sales", ...)
/// configured per-entity in ERP.Persistence/Configurations, but physically share
/// one database/context so that a single <see cref="SaveChangesAsync"/> call can
/// commit a cross-module transaction atomically (e.g. Sales Invoice + Inventory
/// movement + Journal Entry all-or-nothing).
///
/// Implements <see cref="IUnitOfWork"/> directly rather than wrapping it, since
/// EF Core's DbContext already IS a Unit of Work by design — adding another layer
/// on top would be redundant indirection, not a real pattern.
/// </summary>
public class AppDbContext : DbContext, IUnitOfWork, ITrialBalanceReadContext
{
    private readonly IMediator? _mediator;
    private readonly ICurrentUserAccessor? _currentUser;
    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;

    public AppDbContext(DbContextOptions<AppDbContext> options, IMediator? mediator = null, ICurrentUserAccessor? currentUser = null)
        : base(options)
    {
        _mediator = mediator;
        _currentUser = currentUser;
    }

    // Accounting module aggregates
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<CostCenter> CostCenters => Set<CostCenter>();
    public DbSet<FiscalYear> FiscalYears => Set<FiscalYear>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();
    public DbSet<NumberSequence> NumberSequences => Set<NumberSequence>();

    /// <summary>Flattened read-model projection backing the Trial Balance query,
    /// implemented as a keyless EF Core entity mapped to a SQL view (see
    /// OnModelCreating). This keeps reporting queries off the transactional
    /// tables' change tracker entirely — pure CQRS read side.</summary>
    public IQueryable<TrialBalanceProjection> TrialBalanceRows => Set<TrialBalanceProjection>().AsNoTracking();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        modelBuilder.Entity<NumberSequence>(b =>
        {
            b.ToTable("NumberSequences", schema: "Accounting");
            b.HasKey(x => x.SequenceKey);
            b.Property(x => x.SequenceKey).HasMaxLength(100);
        });

        // Keyless entity mapped onto a database view (created in a migration) that
        // joins JournalLines -> JournalEntries -> Accounts, pre-filtered to
        // Status = Posted. Avoids re-deriving this join in every report handler.
        modelBuilder.Entity<TrialBalanceProjection>(b =>
        {
            b.HasNoKey();
            b.ToView("vw_TrialBalance", schema: "Accounting");
        });
    }

    /// <summary>
    /// Applies audit-stamping (Created/Updated by + timestamp) automatically for
    /// every tracked entity before persisting, and dispatches domain events raised
    /// by aggregates AFTER a successful commit — never before, so a rolled-back
    /// transaction can never leak an event (e.g. "journal posted" notification)
    /// for data that was never actually saved.
    /// </summary>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var utcNow = DateTime.UtcNow;
        var actor = _currentUser?.UserName ?? "system";

        foreach (var entry in ChangeTracker.Entries<IAuditable>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedDate = utcNow;
                entry.Entity.CreatedBy = actor;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedDate = utcNow;
                entry.Entity.UpdatedBy = actor;
            }
        }

        // Soft-delete interception: a Remove() call is rewritten to a flag update
        // so no financial or operational row is ever physically deleted.
        foreach (var entry in ChangeTracker.Entries<ISoftDeletable>())
        {
            if (entry.State == EntityState.Deleted)
            {
                entry.State = EntityState.Modified;
                entry.Entity.IsDeleted = true;
                entry.Entity.DeletedDate = utcNow;
                entry.Entity.DeletedBy = actor;
            }
        }

        var aggregatesWithEvents = ChangeTracker.Entries<BaseEntity>()
            .Select(e => e.Entity)
            .Where(e => e.DomainEvents.Any())
            .ToList();

        var result = await base.SaveChangesAsync(cancellationToken);

        if (_mediator is not null)
        {
            foreach (var aggregate in aggregatesWithEvents)
            {
                var events = aggregate.DomainEvents.ToList();
                aggregate.ClearDomainEvents();
                foreach (var domainEvent in events)
                    await _mediator.Publish(domainEvent, cancellationToken);
            }
        }

        return result;
    }

    public async Task BeginTransactionAsync(CancellationToken ct = default) =>
        _currentTransaction = await Database.BeginTransactionAsync(ct);

    public async Task CommitTransactionAsync(CancellationToken ct = default)
    {
        try
        {
            await SaveChangesAsync(ct);
            if (_currentTransaction is not null) await _currentTransaction.CommitAsync(ct);
        }
        finally
        {
            if (_currentTransaction is not null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }
        }
    }

    public async Task RollbackTransactionAsync(CancellationToken ct = default)
    {
        if (_currentTransaction is not null)
        {
            await _currentTransaction.RollbackAsync(ct);
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
    }
}

/// <summary>Abstraction over "who is making this change", implemented in
/// ERP.WebAPI against the authenticated JWT principal. Kept as an interface here
/// so Persistence never takes a dependency on ASP.NET Core's HttpContext.</summary>
public interface ICurrentUserAccessor
{
    string? UserName { get; }
    Guid? CompanyId { get; }
    Guid? BranchId { get; }
}

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers the EF Core DbContext against SQL Server using the
    /// connection string named "DefaultConnection", plus retry-on-failure —
    /// required for resilience against transient Azure SQL / SQL Server
    /// connection drops in production.</summary>
    public static IServiceCollection AddErpPersistence(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(maxRetryCount: 3);
                sql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
            }));

        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
        return services;
    }
}
