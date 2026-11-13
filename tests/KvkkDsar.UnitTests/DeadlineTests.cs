using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KvkkDsar.Deadlines;

namespace KvkkDsar.UnitTests;

public sealed class DeadlineTests
{
    private static DateTimeOffset Tr(int y, int m, int d, int h = 10) => new(y, m, d, h, 0, 0, TimeSpan.FromHours(3));

    // received (TR local), strict due, rolled due, reason
    public static TheoryData<DateTimeOffset, string, string, string?> Cases => new()
    {
        { Tr(2026, 11, 13), "2026-12-13", "2026-12-14", "Sunday" },
        { Tr(2026, 2, 18), "2026-03-20", "2026-03-23", "Ramazan Bayramı" },
        { Tr(2026, 4, 27), "2026-05-27", "2026-06-01", "Kurban Bayramı" },
        { Tr(2026, 9, 29), "2026-10-29", "2026-10-30", "Official holiday" },
        { Tr(2026, 4, 26), "2026-05-26", "2026-05-26", null }, // arife is a half day: still a working day
        { Tr(2026, 12, 15), "2027-01-14", "2027-01-14", null },
        { Tr(2027, 2, 6), "2027-03-08", "2027-03-08", null },   // Ramazan arife 2027
        { Tr(2027, 2, 7), "2027-03-09", "2027-03-12", "Ramazan Bayramı" },
        { new DateTimeOffset(2026, 3, 31, 22, 30, 0, TimeSpan.Zero), "2026-05-01", "2026-05-04", "Official holiday" }, // 01:30 on 1 April in Türkiye
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Thirty_days_from_the_day_after_receipt(DateTimeOffset received, string strict, string rolled, string? reason)
    {
        var s = DeadlineCalculator.Compute(received, DeadlinePolicy.Strict);
        var r = DeadlineCalculator.Compute(received, DeadlinePolicy.RollToNextWorkingDay);

        s.DueOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Should().Be(strict);
        s.RolledBecause.Should().BeNull();
        r.DueOn.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture).Should().Be(rolled);
        r.RolledBecause.Should().Be(reason);
    }

    [Fact]
    public void Due_by_is_the_last_tick_of_the_local_due_day()
    {
        var d = DeadlineCalculator.Compute(Tr(2026, 11, 1), DeadlinePolicy.Strict);
        d.DueBy.Should().Be(new DateTimeOffset(2026, 12, 2, 0, 0, 0, TimeSpan.FromHours(3)).AddTicks(-1));
        DeadlineCalculator.Stage(d, d.DueBy).Should().Be(DeadlineStage.AtRisk);
        DeadlineCalculator.Stage(d, d.DueBy.AddTicks(1)).Should().Be(DeadlineStage.Overdue);
    }

    [Fact]
    public void Stages_follow_days_left()
    {
        var d = DeadlineCalculator.Compute(Tr(2026, 11, 1), DeadlinePolicy.Strict); // due 2026-12-01
        DeadlineCalculator.Stage(d, Tr(2026, 11, 23)).Should().Be(DeadlineStage.OnTrack);
        DeadlineCalculator.DaysLeft(d, Tr(2026, 11, 24)).Should().Be(7);
        DeadlineCalculator.Stage(d, Tr(2026, 11, 24)).Should().Be(DeadlineStage.AtRisk);
        DeadlineCalculator.Stage(d, Tr(2026, 12, 2, 0)).Should().Be(DeadlineStage.Overdue);
        DeadlineCalculator.DaysLeft(d, Tr(2026, 12, 5)).Should().Be(-4);
    }

    [Fact]
    public void Strict_never_needs_the_holiday_table_but_rolling_refuses_to_guess()
    {
        DeadlineCalculator.Compute(Tr(2027, 12, 10), DeadlinePolicy.Strict).DueOn.Should().Be(new DateOnly(2028, 1, 9));
        FluentActions.Invoking(() => DeadlineCalculator.Compute(Tr(2027, 12, 10), DeadlinePolicy.RollToNextWorkingDay))
            .Should().Throw<ArgumentOutOfRangeException>().WithMessage("*2028*");
    }

    [Fact]
    public void Complaint_window_is_the_earlier_of_the_two_article_14_limits()
    {
        DeadlineCalculator.ComplaintWindowEnds(Tr(2026, 1, 1), null).Should().Be(new DateOnly(2026, 3, 2));
        DeadlineCalculator.ComplaintWindowEnds(Tr(2026, 1, 1), Tr(2026, 1, 10)).Should().Be(new DateOnly(2026, 2, 9));
        DeadlineCalculator.ComplaintWindowEnds(Tr(2026, 1, 1), Tr(2026, 2, 20)).Should().Be(new DateOnly(2026, 3, 2));
    }

    [Property(MaxTest = 1000)]
    public Property Deadline_invariants_hold_for_any_receipt_time() =>
        Prop.ForAll(Gen.Choose(0, (365 * 2) + 300).ToArbitrary(), Gen.Choose(0, 86_399).ToArbitrary(), (day, second) =>
        {
            var received = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day).AddSeconds(second);
            var strict = DeadlineCalculator.Compute(received, DeadlinePolicy.Strict);
            var rolled = DeadlineCalculator.Compute(received, DeadlinePolicy.RollToNextWorkingDay);
            var cal = TurkishCalendar.Default;
            var gapAllHolidays = true;
            for (var d = rolled.NominalDueOn; d < rolled.DueOn; d = d.AddDays(1))
            {
                gapAllHolidays &= !DeadlineCalculator.IsWorkingDay(d, cal);
            }

            return strict.DueOn == strict.ReceivedOn.AddDays(30)
                && strict.DueOn == strict.NominalDueOn
                && rolled.DueOn >= rolled.NominalDueOn
                && rolled.DueOn.DayNumber - rolled.NominalDueOn.DayNumber <= 9
                && DeadlineCalculator.IsWorkingDay(rolled.DueOn, cal)
                && gapAllHolidays
                && DeadlineCalculator.LocalDate(strict.DueBy) == strict.DueOn
                && strict.DueBy > received;
        });
}
