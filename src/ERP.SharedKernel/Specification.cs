using System;
using System.Collections.Generic;
using System.Linq.Expressions;

namespace ERP.SharedKernel;

/// <summary>
/// Specification Pattern implementation. Encapsulates a reusable, composable,
/// testable query rule (e.g. "OverdueInvoicesSpecification",
/// "ActiveAccountsInGroupSpecification") independent of the repository/EF Core
/// implementation. Repositories accept an <see cref="ISpecification{T}"/> and
/// translate it into an IQueryable, keeping query logic out of controllers/handlers
/// and colocated with the domain concept it represents.
/// </summary>
public interface ISpecification<T>
{
    Expression<Func<T, bool>>? Criteria { get; }
    List<Expression<Func<T, object>>> Includes { get; }
    List<string> IncludeStrings { get; }
    Expression<Func<T, object>>? OrderBy { get; }
    Expression<Func<T, object>>? OrderByDescending { get; }
    int Take { get; }
    int Skip { get; }
    bool IsPagingEnabled { get; }
}

public abstract class Specification<T> : ISpecification<T>
{
    protected Specification() { }

    protected Specification(Expression<Func<T, bool>> criteria) => Criteria = criteria;

    public Expression<Func<T, bool>>? Criteria { get; private set; }
    public List<Expression<Func<T, object>>> Includes { get; } = new();
    public List<string> IncludeStrings { get; } = new();
    public Expression<Func<T, object>>? OrderBy { get; private set; }
    public Expression<Func<T, object>>? OrderByDescending { get; private set; }
    public int Take { get; private set; }
    public int Skip { get; private set; }
    public bool IsPagingEnabled { get; private set; }

    protected void AddCriteria(Expression<Func<T, bool>> criteria) => Criteria = criteria;
    protected void AddInclude(Expression<Func<T, object>> includeExpression) => Includes.Add(includeExpression);
    protected void AddInclude(string includeString) => IncludeStrings.Add(includeString);
    protected void ApplyOrderBy(Expression<Func<T, object>> orderByExpression) => OrderBy = orderByExpression;
    protected void ApplyOrderByDescending(Expression<Func<T, object>> expr) => OrderByDescending = expr;

    protected void ApplyPaging(int skip, int take)
    {
        Skip = skip;
        Take = take;
        IsPagingEnabled = true;
    }
}

/// <summary>Generic repository contract implemented per-aggregate in Persistence.
/// Only Aggregate Roots get repositories — child entities are persisted via their
/// root, per DDD aggregate-boundary rules.</summary>
public interface IRepository<T> where T : AggregateRoot
{
    Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task<int> CountAsync(ISpecification<T> spec, CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Update(T entity);
    void Remove(T entity); // performs soft delete
}

/// <summary>Unit of Work — coordinates commits across multiple repositories within
/// a single business transaction (e.g. posting a Sales Invoice touches
/// SalesInvoice, InventoryTransaction, and JournalEntry repositories atomically).</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
    Task BeginTransactionAsync(CancellationToken ct = default);
    Task CommitTransactionAsync(CancellationToken ct = default);
    Task RollbackTransactionAsync(CancellationToken ct = default);
}
