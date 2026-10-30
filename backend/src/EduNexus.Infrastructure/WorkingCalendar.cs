using EduNexus.Foundation;
using Microsoft.EntityFrameworkCore;

namespace EduNexus.Infrastructure;

/// <summary>
/// The institution's working week and clock, for deadlines counted in working days. The weekend is a setting (default Friday and
/// Saturday), and the holidays are the calendar events of kind Holiday, so a deadline never lands inside a holiday.
/// </summary>
public sealed class WorkingCalendar(IReadOnlySet<DayOfWeek> weekend, TimeSpan utcOffset)
{
    public static readonly WorkingCalendar Default = new(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, TimeSpan.FromHours(3));

    public IReadOnlySet<DayOfWeek> Weekend => weekend;
    public TimeSpan UtcOffset => utcOffset;

    /// <summary>"Friday,Saturday" (names, any case) to a weekend; anything unreadable gives the default so a typo never removes the weekend.</summary>
    public static IReadOnlySet<DayOfWeek> ParseWeekend(string? text)
    {
        var days = new HashSet<DayOfWeek>();
        foreach (var part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Enum.TryParse<DayOfWeek>(part, true, out var d) || int.TryParse(part, out _)) return Default.Weekend;
            days.Add(d);
        }
        return days.Count is > 0 and < 7 ? days : Default.Weekend;
    }

    /// <summary>Pure rule: the date after counting <paramref name="days"/> working days from <paramref name="from"/>, skipping weekends and the given holidays.</summary>
    public DateTimeOffset AddWorkingDays(DateTimeOffset from, int days, IReadOnlySet<DateOnly> holidays)
    {
        var local = from.ToOffset(utcOffset);
        if (days <= 0) return from;
        var date = DateOnly.FromDateTime(local.DateTime);
        var left = days;
        while (left > 0)
        {
            date = date.AddDays(1);
            if (!weekend.Contains(date.DayOfWeek) && !holidays.Contains(date)) left--;
        }
        // Stored times are UTC (the database accepts no other offset); the date is counted on the institution's clock.
        return new DateTimeOffset(date.ToDateTime(TimeOnly.FromTimeSpan(local.TimeOfDay)), utcOffset).ToUniversalTime();
    }

    /// <summary>The holidays that could matter for a deadline of this many working days from now. Call inside a tenant scope.</summary>
    public async Task<DateTimeOffset> AddWorkingDaysAsync(AppDbContext db, Guid tenantId, DateTimeOffset from, int days, CancellationToken ct)
    {
        if (days <= 0) return from;
        var horizon = from.AddDays(Math.Min(days, 365) * 3 + 60);
        var events = await db.CalendarEvents.Where(e => e.TenantId == tenantId && e.Kind == CalendarEventKind.Holiday && e.EndsAt >= from.AddDays(-1) && e.StartsAt <= horizon).ToListAsync(ct);
        var holidays = new HashSet<DateOnly>();
        foreach (var e in events)
        {
            var start = DateOnly.FromDateTime(e.StartsAt.ToOffset(utcOffset).DateTime);
            var endLocal = e.EndsAt.ToOffset(utcOffset);
            var end = DateOnly.FromDateTime(endLocal.DateTime);
            if (endLocal.TimeOfDay == TimeSpan.Zero && end > start) end = end.AddDays(-1);   // an event that ends at midnight does not take that day
            for (var d = start; d <= end && holidays.Count < 3650; d = d.AddDays(1)) holidays.Add(d);
        }
        return AddWorkingDays(from, days, holidays);
    }
}
