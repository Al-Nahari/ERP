using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP.Domain.Accounting.Services;
using ERP.SharedKernel;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace ERP.Application.Accounting.Queries.GetTrialBalance;

public record TrialBalanceLine(string AccountCode, string AccountNameEn, string AccountNameAr,
    decimal TotalDebit, decimal TotalCredit, decimal Balance);

public record GetTrialBalanceQuery(Guid CompanyId, DateTime AsOfDate) : IRequest<Result<List<TrialBalanceLine>>>;

/// <summary>
/// Read-model query context — separate, deliberately minimal, read-only DbContext
/// interface (CQRS read side) so reporting queries never share change-tracking
/// overhead with the write side. Implemented against the same physical database
/// via EF Core's read-only query projections in Persistence.
/// </summary>
public interface ITrialBalanceReadContext
{
    IQueryable<TrialBalanceProjection> TrialBalanceRows { get; }
}

public record TrialBalanceProjection(Guid CompanyId, string AccountCode, string AccountNameEn,
    string AccountNameAr, DateTime PostingDate, decimal Debit, decimal Credit, bool IsPosted);

/// <summary>
/// Handler computes the Trial Balance by summing all posted journal lines per
/// account up to AsOfDate. Result is cached in Redis (see
/// CachedTrialBalanceQueryHandler decorator in Infrastructure, applying the
/// Decorator pattern) keyed by CompanyId+AsOfDate, invalidated whenever a new
/// JournalEntryPostedEvent fires for that company.
/// </summary>
public class GetTrialBalanceQueryHandler : IRequestHandler<GetTrialBalanceQuery, Result<List<TrialBalanceLine>>>
{
    private readonly ITrialBalanceReadContext _readContext;

    public GetTrialBalanceQueryHandler(ITrialBalanceReadContext readContext)
    {
        _readContext = readContext;
    }

    public async Task<Result<List<TrialBalanceLine>>> Handle(GetTrialBalanceQuery request, CancellationToken cancellationToken)
    {
        var rows = await _readContext.TrialBalanceRows
            .Where(r => r.CompanyId == request.CompanyId
                        && r.IsPosted
                        && r.PostingDate <= request.AsOfDate)
            .ToListAsync(cancellationToken);

        var result = rows
            .GroupBy(r => new { r.AccountCode, r.AccountNameEn, r.AccountNameAr })
            .Select(g => new TrialBalanceLine(
                g.Key.AccountCode, g.Key.AccountNameEn, g.Key.AccountNameAr,
                g.Sum(x => x.Debit), g.Sum(x => x.Credit), g.Sum(x => x.Debit) - g.Sum(x => x.Credit)))
            .OrderBy(l => l.AccountCode)
            .ToList();

        return Result.Success(result);
    }
}
