namespace KvkkDsar.Deadlines;

/// <summary>
/// Türkiye's official holidays (Law No. 2429). Religious holidays follow the Hijri calendar and are listed per year;
/// the eves (arife) of the two bayrams and 28 October are half days (holiday from 13:00), which still count as
/// working days for a deadline. Years outside the table make the calendar refuse to answer rather than guess.
/// </summary>
public sealed class TurkishCalendar
{
    private static readonly (int Month, int Day)[] FixedHolidays =
    [
        (1, 1),   // Yılbaşı
        (4, 23),  // Ulusal Egemenlik ve Çocuk Bayramı
        (5, 1),   // Emek ve Dayanışma Günü
        (5, 19),  // Atatürk'ü Anma, Gençlik ve Spor Bayramı
        (7, 15),  // Demokrasi ve Millî Birlik Günü
        (8, 30),  // Zafer Bayramı
        (10, 29), // Cumhuriyet Bayramı
    ];

    // Ramazan Bayramı (3 days) and Kurban Bayramı (4 days), first day per year; sources in docs/adr/0003.
    private static readonly Dictionary<int, (DateOnly Ramazan, DateOnly Kurban)> Published = new()
    {
        [2025] = (new DateOnly(2025, 3, 30), new DateOnly(2025, 6, 6)),
        [2026] = (new DateOnly(2026, 3, 20), new DateOnly(2026, 5, 27)),
        [2027] = (new DateOnly(2027, 3, 9), new DateOnly(2027, 5, 16)),
    };

    private readonly Dictionary<int, (DateOnly Ramazan, DateOnly Kurban)> _religious;

    public TurkishCalendar(IReadOnlyDictionary<int, (DateOnly Ramazan, DateOnly Kurban)>? religiousHolidays = null) =>
        _religious = new(religiousHolidays ?? Published);

    public static TurkishCalendar Default { get; } = new();

    public int FirstYear => _religious.Keys.Min();

    public int LastYear => _religious.Keys.Max();

    public bool Covers(DateOnly date) => _religious.ContainsKey(date.Year);

    public bool IsOfficialHoliday(DateOnly date)
    {
        if (!_religious.TryGetValue(date.Year, out var r))
        {
            throw new ArgumentOutOfRangeException(nameof(date), date,
                $"The holiday table covers {FirstYear}-{LastYear}; add the religious holidays for {date.Year} before computing deadlines in it.");
        }

        return FixedHolidays.Contains((date.Month, date.Day))
            || (date >= r.Ramazan && date < r.Ramazan.AddDays(3))
            || (date >= r.Kurban && date < r.Kurban.AddDays(4));
    }

    public string? HolidayName(DateOnly date)
    {
        if (!IsOfficialHoliday(date))
        {
            return null;
        }

        var r = _religious[date.Year];
        return date >= r.Ramazan && date < r.Ramazan.AddDays(3) ? "Ramazan Bayramı"
            : date >= r.Kurban && date < r.Kurban.AddDays(4) ? "Kurban Bayramı"
            : "Official holiday";
    }
}

/// <summary>How the last day of the 30-day period is treated.</summary>
public enum DeadlinePolicy
{
    /// <summary>
    /// Day 30 is the deadline even if it is a weekend or holiday. The conservative reading for a controller, and the
    /// default: a response sent early is never late.
    /// </summary>
    Strict,

    /// <summary>
    /// If day 30 falls on a Saturday, Sunday or official holiday, the deadline moves to the next working day
    /// (Turkish Code of Obligations Art. 93 / Code of Civil Procedure Art. 93 reasoning). Use only with legal sign-off.
    /// </summary>
    RollToNextWorkingDay,
}

public enum DeadlineStage
{
    OnTrack,
    AtRisk,
    Overdue,
}

/// <summary>The computed deadline of one request. Dates are Türkiye local dates (UTC+03:00).</summary>
public sealed record Deadline(
    DateOnly ReceivedOn,
    DateOnly NominalDueOn,
    DateOnly DueOn,
    DateTimeOffset DueBy,
    DeadlinePolicy Policy,
    string? RolledBecause);

/// <summary>
/// KVKK Art. 13(2): the controller concludes the application "as soon as possible and within thirty days at the
/// latest". The day of receipt is not counted (TBK Art. 92 counting), so an application received on 1 March is due
/// by the end of 31 March. The clock does not stop while identity is being verified: nothing in the law pauses it,
/// so the system does not either.
/// </summary>
public static class DeadlineCalculator
{
    public const int ResponseDays = 30;
    public const int AtRiskDaysLeft = 7;

    /// <summary>KVKK Art. 14(2): complaint to the Board within 30 days of the response, and within 60 days of the application.</summary>
    public const int ComplaintDaysAfterResponse = 30;
    public const int ComplaintDaysAfterApplication = 60;

    /// <summary>Türkiye has stayed on UTC+03:00 all year since September 2016; no tz database is needed.</summary>
    public static readonly TimeSpan TurkeyOffset = TimeSpan.FromHours(3);

    public static DateOnly LocalDate(DateTimeOffset instant) => DateOnly.FromDateTime(instant.ToOffset(TurkeyOffset).DateTime);

    public static Deadline Compute(DateTimeOffset receivedAt, DeadlinePolicy policy, TurkishCalendar? calendar = null)
    {
        calendar ??= TurkishCalendar.Default;
        var received = LocalDate(receivedAt);
        var nominal = received.AddDays(ResponseDays);
        var due = nominal;
        string? because = null;

        if (policy == DeadlinePolicy.RollToNextWorkingDay)
        {
            while (!IsWorkingDay(due, calendar))
            {
                because ??= calendar.HolidayName(due) ?? due.DayOfWeek.ToString();
                due = due.AddDays(1);
            }
        }

        // The deadline is the end of the local day: 23:59:59.999 in Türkiye, i.e. the start of the next day exclusive.
        var dueBy = new DateTimeOffset(due.AddDays(1).ToDateTime(TimeOnly.MinValue), TurkeyOffset).AddTicks(-1);
        return new Deadline(received, nominal, due, dueBy, policy, because);
    }

    public static bool IsWorkingDay(DateOnly date, TurkishCalendar calendar) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !calendar.IsOfficialHoliday(date);

    /// <summary>Whole local days left until the due date (0 on the due date itself, negative once overdue).</summary>
    public static int DaysLeft(Deadline deadline, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        return deadline.DueOn.DayNumber - LocalDate(now).DayNumber;
    }

    public static DeadlineStage Stage(Deadline deadline, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(deadline);
        return now > deadline.DueBy ? DeadlineStage.Overdue
            : DaysLeft(deadline, now) <= AtRiskDaysLeft ? DeadlineStage.AtRisk
            : DeadlineStage.OnTrack;
    }

    /// <summary>The last day the data subject can complain to the Board (the earlier of the two Art. 14 limits).</summary>
    public static DateOnly ComplaintWindowEnds(DateTimeOffset receivedAt, DateTimeOffset? respondedAt)
    {
        var afterApplication = LocalDate(receivedAt).AddDays(ComplaintDaysAfterApplication);
        if (respondedAt is null)
        {
            return afterApplication;
        }

        var afterResponse = LocalDate(respondedAt.Value).AddDays(ComplaintDaysAfterResponse);
        return afterResponse < afterApplication ? afterResponse : afterApplication;
    }
}
