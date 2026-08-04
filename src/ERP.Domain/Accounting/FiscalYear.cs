using System;
using System.Collections.Generic;
using System.Linq;
using ERP.SharedKernel;

namespace ERP.Domain.Accounting;

public enum PeriodStatus
{
    Open = 1,
    Closed = 2,
    Locked = 3 // closed AND archived — no reopening without a reversing-year adjustment
}

/// <summary>
/// Aggregate Root representing one fiscal year, composed of accounting periods
/// (typically monthly). No journal entry may post to a period that is not Open —
/// this is the primary control that prevents accidental/fraudulent tampering with
/// closed books, mirroring period-lock behavior in SAP/Dynamics.
/// </summary>
public class FiscalYear : AggregateRoot
{
    public string Name { get; private set; } = default!;      // e.g. "FY2026"
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public Guid CompanyId { get; private set; }
    public bool IsClosed { get; private set; }

    private readonly List<AccountingPeriod> _periods = new();
    public IReadOnlyCollection<AccountingPeriod> Periods => _periods.AsReadOnly();

    private FiscalYear() { }

    public static Result<FiscalYear> CreateWithMonthlyPeriods(string name, DateTime startDate, DateTime endDate, Guid companyId)
    {
        if (endDate <= startDate)
            return Result.Failure<FiscalYear>("Fiscal year end date must be after start date.");

        var fy = new FiscalYear { Name = name, StartDate = startDate, EndDate = endDate, CompanyId = companyId };

        var cursor = new DateTime(startDate.Year, startDate.Month, 1);
        int seq = 1;
        while (cursor <= endDate)
        {
            var periodEnd = cursor.AddMonths(1).AddDays(-1);
            if (periodEnd > endDate) periodEnd = endDate;
            fy._periods.Add(AccountingPeriod.Create(fy.Id, seq, cursor, periodEnd));
            cursor = cursor.AddMonths(1);
            seq++;
        }
        return Result.Success(fy);
    }

    public Result<AccountingPeriod> GetPeriodForDate(DateTime date)
    {
        var period = _periods.FirstOrDefault(p => date >= p.StartDate && date <= p.EndDate);
        return period is null
            ? Result.Failure<AccountingPeriod>($"No accounting period found for date {date:yyyy-MM-dd} in fiscal year {Name}.")
            : Result.Success(period);
    }

    /// <summary>Closes a period after validating all prior periods are already
    /// closed — periods must close sequentially, matching real-world close
    /// procedures (cannot close March while January is still open).</summary>
    public Result ClosePeriod(int periodNumber, string closedBy)
    {
        var ordered = _periods.OrderBy(p => p.SequenceNumber).ToList();
        var target = ordered.FirstOrDefault(p => p.SequenceNumber == periodNumber);
        if (target is null) return Result.Failure("Period not found.");

        var priorOpen = ordered.Where(p => p.SequenceNumber < periodNumber && p.Status == PeriodStatus.Open).Any();
        if (priorOpen)
            return Result.Failure("Cannot close this period while an earlier period is still open. Periods must close in sequence.");

        target.Close(closedBy);
        return Result.Success();
    }
}

/// <summary>Child entity of FiscalYear — the actual posting-control unit. Every
/// JournalEntry line is validated against its period's Status before posting.</summary>
public class AccountingPeriod : BaseEntity
{
    public Guid FiscalYearId { get; private set; }
    public int SequenceNumber { get; private set; }
    public DateTime StartDate { get; private set; }
    public DateTime EndDate { get; private set; }
    public PeriodStatus Status { get; private set; } = PeriodStatus.Open;
    public DateTime? ClosedDate { get; private set; }
    public string? ClosedBy { get; private set; }

    private AccountingPeriod() { }

    internal static AccountingPeriod Create(Guid fiscalYearId, int seq, DateTime start, DateTime end) => new()
    {
        FiscalYearId = fiscalYearId,
        SequenceNumber = seq,
        StartDate = start,
        EndDate = end
    };

    internal void Close(string closedBy)
    {
        Status = PeriodStatus.Closed;
        ClosedDate = DateTime.UtcNow;
        ClosedBy = closedBy;
    }

    public Result EnsureOpenForPosting()
    {
        if (Status != PeriodStatus.Open)
            return Result.Failure($"Accounting period {StartDate:yyyy-MM} is {Status} and cannot accept new postings.");
        return Result.Success();
    }
}
