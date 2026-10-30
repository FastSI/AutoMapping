namespace AutoMapping.Reporting;

public enum ReportFrequency { Daily, Weekly, Monthly }

/// <summary>
/// When a report is due: every day, every week on a weekday, or every month on a day, at a local time of day in a time zone.
/// A monthly day that does not exist (31 in February) runs on the month's last day. DST gaps shift the run forward by the gap.
/// </summary>
public sealed class ReportSchedule
{
    private ReportSchedule(string name, ReportFrequency frequency, TimeOnly timeOfDay, DayOfWeek dayOfWeek, int dayOfMonth, TimeZoneInfo zone)
    {
        Name = name;
        Frequency = frequency;
        TimeOfDay = timeOfDay;
        DayOfWeek = dayOfWeek;
        DayOfMonth = dayOfMonth;
        TimeZone = zone;
    }

    public string Name { get; }
    public ReportFrequency Frequency { get; }
    public TimeOnly TimeOfDay { get; }
    public DayOfWeek DayOfWeek { get; }
    public int DayOfMonth { get; }
    public TimeZoneInfo TimeZone { get; }

    public static ReportSchedule Daily(TimeOnly at, TimeZoneInfo? zone = null) => new("daily", ReportFrequency.Daily, at, DayOfWeek.Monday, 1, zone ?? TimeZoneInfo.Utc);

    public static ReportSchedule Weekly(DayOfWeek day, TimeOnly at, TimeZoneInfo? zone = null) => new("weekly", ReportFrequency.Weekly, at, day, 1, zone ?? TimeZoneInfo.Utc);

    public static ReportSchedule Monthly(int dayOfMonth, TimeOnly at, TimeZoneInfo? zone = null)
    {
        if (dayOfMonth is < 1 or > 31) throw new ArgumentOutOfRangeException(nameof(dayOfMonth), "Day of month must be 1 to 31.");
        return new("monthly", ReportFrequency.Monthly, at, DayOfWeek.Monday, dayOfMonth, zone ?? TimeZoneInfo.Utc);
    }

    /// <summary>The first run strictly after <paramref name="moment"/>.</summary>
    public DateTimeOffset NextAfter(DateTimeOffset moment)
    {
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, TimeZone).DateTime);
        for (var i = 0; i <= 400; i++)
        {
            var date = start.AddDays(i);
            if (!Matches(date)) continue;
            var instant = At(date);
            if (instant > moment) return instant;
        }
        throw new InvalidOperationException("No occurrence found within 400 days; the schedule is invalid.");
    }

    /// <summary>The last run strictly before <paramref name="moment"/>.</summary>
    public DateTimeOffset PreviousBefore(DateTimeOffset moment)
    {
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(moment, TimeZone).DateTime);
        for (var i = 0; i <= 400; i++)
        {
            var date = start.AddDays(-i);
            if (!Matches(date)) continue;
            var instant = At(date);
            if (instant < moment) return instant;
        }
        throw new InvalidOperationException("No previous occurrence found within 400 days; the schedule is invalid.");
    }

    private bool Matches(DateOnly date) => Frequency switch
    {
        ReportFrequency.Daily => true,
        ReportFrequency.Weekly => date.DayOfWeek == DayOfWeek,
        _ => date.Day == Math.Min(DayOfMonth, DateTime.DaysInMonth(date.Year, date.Month))
    };

    private DateTimeOffset At(DateOnly date)
    {
        var local = date.ToDateTime(TimeOfDay, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(local)) local = local.AddHours(1);   // spring-forward gap: run just after it
        return new DateTimeOffset(local, TimeZone.GetUtcOffset(local));
    }

    public override string ToString() => Frequency switch
    {
        ReportFrequency.Daily => $"daily at {TimeOfDay:HH:mm} ({TimeZone.Id})",
        ReportFrequency.Weekly => $"weekly on {DayOfWeek} at {TimeOfDay:HH:mm} ({TimeZone.Id})",
        _ => $"monthly on day {DayOfMonth} at {TimeOfDay:HH:mm} ({TimeZone.Id})"
    };
}
