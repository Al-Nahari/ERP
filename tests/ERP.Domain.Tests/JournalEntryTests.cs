using System;
using System.Linq;
using ERP.Domain.Accounting;
using FluentAssertions;
using Xunit;

namespace ERP.Domain.Tests;

/// <summary>
/// Domain tests proving the single most important invariant in the whole system:
/// a JournalEntry can never be posted unless it is balanced. These tests exercise
/// the aggregate in isolation, with no database, no mocks beyond a bare
/// AccountingPeriod — exactly as DDD domain tests should be written.
/// </summary>
public class JournalEntryTests
{
    private static AccountingPeriod OpenPeriod()
    {
        var fy = FiscalYear.CreateWithMonthlyPeriods("FY2026", new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), Guid.NewGuid()).Value;
        return fy.Periods.First();
    }

    [Fact]
    public void Post_WithEqualDebitAndCredit_Succeeds()
    {
        var entry = JournalEntry.CreateDraft("JE-0001", new DateTime(2026, 1, 15), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "Test sale");

        entry.AddLine(Guid.NewGuid(), debit: 1150, credit: 0, "SAR");
        entry.AddLine(Guid.NewGuid(), debit: 0, credit: 1000, "SAR");
        entry.AddLine(Guid.NewGuid(), debit: 0, credit: 150, "SAR");

        var result = entry.Post(OpenPeriod());

        result.IsSuccess.Should().BeTrue();
        entry.Status.Should().Be(JournalEntryStatus.Posted);
        entry.TotalDebit.Should().Be(1150);
        entry.TotalCredit.Should().Be(1150);
    }

    [Fact]
    public void Post_WithUnbalancedLines_Fails()
    {
        var entry = JournalEntry.CreateDraft("JE-0002", new DateTime(2026, 1, 15), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "Unbalanced test");

        entry.AddLine(Guid.NewGuid(), debit: 1000, credit: 0, "SAR");
        entry.AddLine(Guid.NewGuid(), debit: 0, credit: 900, "SAR"); // deliberately short by 100

        var result = entry.Post(OpenPeriod());

        result.IsFailure.Should().BeTrue();
        entry.Status.Should().Be(JournalEntryStatus.Draft); // must NOT transition to Posted
    }

    [Fact]
    public void AddLine_WithBothDebitAndCredit_Fails()
    {
        var entry = JournalEntry.CreateDraft("JE-0003", DateTime.Today, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "Invalid line test");

        var result = entry.AddLine(Guid.NewGuid(), debit: 100, credit: 100, "SAR");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Post_WithFewerThanTwoLines_Fails()
    {
        var entry = JournalEntry.CreateDraft("JE-0004", DateTime.Today, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "Single line test");
        entry.AddLine(Guid.NewGuid(), debit: 500, credit: 0, "SAR");

        var result = entry.Post(OpenPeriod());

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void CreateReversal_MirrorsDebitsAndCredits()
    {
        var entry = JournalEntry.CreateDraft("JE-0005", new DateTime(2026, 1, 10), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), Guid.NewGuid(), "Original entry");
        var accountA = Guid.NewGuid();
        var accountB = Guid.NewGuid();
        entry.AddLine(accountA, 500, 0, "SAR");
        entry.AddLine(accountB, 0, 500, "SAR");
        entry.Post(OpenPeriod());

        var reversalResult = entry.CreateReversal("JE-0005-R", new DateTime(2026, 1, 20), Guid.NewGuid(), "Correction");

        reversalResult.IsSuccess.Should().BeTrue();
        var reversal = reversalResult.Value;
        reversal.Lines.First(l => l.AccountId == accountA).Credit.Should().Be(500);
        reversal.Lines.First(l => l.AccountId == accountB).Debit.Should().Be(500);
        entry.Status.Should().Be(JournalEntryStatus.Reversed);
    }

    [Fact]
    public void Post_ToClosedPeriod_Fails()
    {
        var fy = FiscalYear.CreateWithMonthlyPeriods("FY2026", new DateTime(2026, 1, 1), new DateTime(2026, 12, 31), Guid.NewGuid()).Value;
        fy.ClosePeriod(1, "controller@company.com");
        var closedPeriod = fy.Periods.First(p => p.SequenceNumber == 1);

        var entry = JournalEntry.CreateDraft("JE-0006", new DateTime(2026, 1, 15), fy.Id, closedPeriod.Id,
            Guid.NewGuid(), Guid.NewGuid(), "Late posting attempt");
        entry.AddLine(Guid.NewGuid(), 100, 0, "SAR");
        entry.AddLine(Guid.NewGuid(), 0, 100, "SAR");

        var result = entry.Post(closedPeriod);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().Contain(e => e.Contains("cannot accept new postings"));
    }
}
