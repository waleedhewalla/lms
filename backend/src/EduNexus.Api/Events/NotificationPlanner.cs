using EduNexus.Foundation;

namespace EduNexus.Api.Events;

/// <summary>
/// BBP M10 priority→channel matrix + template rendering.
/// FYI: InApp · Normal: InApp+Email · Important: +Push · Urgent: +SMS · Emergency: all.
/// Push/SMS/Teams have no provider wired yet: rows are recorded as Queued with
/// provider note, so channel coverage is honest and retryable later.
/// </summary>
public static class NotificationPlanner
{
    public static NotificationChannel[] ChannelsFor(NotificationPriority priority) => priority switch
    {
        NotificationPriority.FYI => [NotificationChannel.InApp],
        NotificationPriority.Normal => [NotificationChannel.InApp, NotificationChannel.Email],
        NotificationPriority.Important => [NotificationChannel.InApp, NotificationChannel.Email],
        NotificationPriority.Urgent => [NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms],
        NotificationPriority.Emergency => [NotificationChannel.InApp, NotificationChannel.Email, NotificationChannel.Sms],
        _ => [NotificationChannel.InApp],
    };

    public static NotificationPriority PriorityFor(string eventType, bool accelerated) => (eventType, accelerated) switch
    {
        (_, true) => NotificationPriority.Urgent,
        ("TaskBreached", _) => NotificationPriority.Important,
        ("DecisionPublished", _) => NotificationPriority.Important,
        ("PolicyPublished", _) => NotificationPriority.Normal,
        _ => NotificationPriority.Normal,
    };

    /// <summary>Renders {token} placeholders from a case-insensitive value map.</summary>
    public static string Render(string template, IReadOnlyDictionary<string, string> values)
    {
        var result = template;
        foreach (var (k, v) in values)
            result = result.Replace($"{{{k}}}", v, StringComparison.OrdinalIgnoreCase);
        return result;
    }
}
