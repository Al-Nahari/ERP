using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace ERP.Persistence.Repositories;

/// <summary>Translates an <see cref="ISpecification{T}"/> into an EF Core
/// IQueryable — the piece that lets the Specification pattern stay
/// persistence-ignorant in the Domain/Application layers while still compiling
/// down to efficient SQL here.</summary>
public static class SpecificationEvaluator<T> where T : class
{
    public static IQueryable<T> GetQuery(IQueryable<T> inputQuery, ISpecification<T> spec)
    {
        var query = inputQuery;

        if (spec.Criteria is not null)
            query = query.Where(spec.Criteria);

        query = spec.Includes.Aggregate(query, (current, include) => current.Include(include));
        query = spec.IncludeStrings.Aggregate(query, (current, include) => current.Include(include));

        if (spec.OrderBy is not null) query = query.OrderBy(spec.OrderBy);
        else if (spec.OrderByDescending is not null) query = query.OrderByDescending(spec.OrderByDescending);

        if (spec.IsPagingEnabled) query = query.Skip(spec.Skip).Take(spec.Take);

        return query;
    }
}

/// <summary>Generic EF Core repository implementing <see cref="IRepository{T}"/>
/// for any Aggregate Root. Module-specific repositories (see
/// <c>AccountRepository</c>, <c>JournalEntryRepository</c>) derive from this and
/// add only the queries that don't fit the generic Specification pattern (e.g.
/// sequence-number generation, which needs a raw atomic SQL operation).</summary>
public class Repository<T> : IRepository<T> where T : AggregateRoot
{
    protected readonly AppDbContext Context;
    public Repository(AppDbContext context) => Context = context;

    public async Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await Context.Set<T>().FindAsync(new object[] { id }, ct);

    public async Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken ct = default) =>
        await SpecificationEvaluator<T>.GetQuery(Context.Set<T>().AsQueryable(), spec).ToListAsync(ct);

    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken ct = default) =>
        await SpecificationEvaluator<T>.GetQuery(Context.Set<T>().AsQueryable(), spec).FirstOrDefaultAsync(ct);

    public async Task<int> CountAsync(ISpecification<T> spec, CancellationToken ct = default) =>
        await SpecificationEvaluator<T>.GetQuery(Context.Set<T>().AsQueryable(), spec).CountAsync(ct);

    public async Task AddAsync(T entity, CancellationToken ct = default) =>
        await Context.Set<T>().AddAsync(entity, ct);

    public void Update(T entity) => Context.Entry(entity).State = EntityState.Modified;

    /// <summary>Soft-delete: EF Core's SaveChanges override in AppDbContext
    /// rewrites this Deleted state into a flag update, so no row is ever
    /// physically removed.</summary>
    public void Remove(T entity) => Context.Set<T>().Remove(entity);
}
