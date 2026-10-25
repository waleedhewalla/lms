using System.Globalization;

namespace EduNexus.Foundation;

/// <summary>
/// Umm al-Qura (official Saudi) Hijri dates. A Hijri day depends on the local calendar day, so instants are
/// shifted to the institution's UTC offset first (Saudi Arabia is UTC+3 all year, no daylight saving).
/// A fixed offset is used on purpose: the Alpine runtime image ships without time-zone data.
/// </summary>
public static class HijriDate
{
    private static readonly UmAlQuraCalendar Calendar = new();

    /// <summary>"yyyy-MM-dd" in the Umm al-Qura calendar, or null outside its supported range (1900–2077).</summary>
    public static string? Format(DateTimeOffset instant, TimeSpan utcOffset)
    {
        var local = instant.ToOffset(utcOffset).DateTime;
        if (local < Calendar.MinSupportedDateTime || local > Calendar.MaxSupportedDateTime) return null;
        return $"{Calendar.GetYear(local):D4}-{Calendar.GetMonth(local):D2}-{Calendar.GetDayOfMonth(local):D2}";
    }

    public static string? Format(DateTimeOffset instant) => Format(instant, SaudiOffset);

    public static readonly TimeSpan SaudiOffset = TimeSpan.FromHours(3);
}
