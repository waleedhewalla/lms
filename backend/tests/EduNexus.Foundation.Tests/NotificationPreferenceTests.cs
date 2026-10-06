namespace EduNexus.Foundation.Tests;

public class NotificationPreferenceTests
{
    private static readonly Guid T = Guid.NewGuid(), P = Guid.NewGuid();
    private static DateTimeOffset At(int hourUtc) => new(2026, 10, 6, hourUtc, 15, 0, TimeSpan.Zero);
    private static NotificationPreference Pref(bool enabled = true, NotificationPriority min = NotificationPriority.FYI, int? from = null, int? to = null) =>
        new(Guid.NewGuid(), T, P, NotificationChannel.Email, enabled, min, from, to);

    [Fact]
    public void InApp_And_Emergency_Always_Delivered()
    {
        Assert.True(NotificationPreference.Allows(Pref(enabled: false), NotificationChannel.InApp, NotificationPriority.FYI, At(3)));
        Assert.True(NotificationPreference.Allows(Pref(enabled: false), NotificationChannel.Email, NotificationPriority.Emergency, At(3)));
    }

    [Fact]
    public void No_Preference_Means_Defaults()
        => Assert.True(NotificationPreference.Allows(null, NotificationChannel.Email, NotificationPriority.FYI, At(3)));

    [Fact]
    public void Disabled_And_Below_Minimum_Are_Suppressed()
    {
        Assert.False(NotificationPreference.Allows(Pref(enabled: false), NotificationChannel.Email, NotificationPriority.Normal, At(12)));
        Assert.False(NotificationPreference.Allows(Pref(min: NotificationPriority.Important), NotificationChannel.Email, NotificationPriority.Normal, At(12)));
        Assert.True(NotificationPreference.Allows(Pref(min: NotificationPriority.Important), NotificationChannel.Email, NotificationPriority.Important, At(12)));
    }

    [Fact]
    public void Quiet_Hours_Wrap_Midnight_And_Urgent_Overrides()
    {
        var night = Pref(from: 22, to: 6);
        Assert.False(NotificationPreference.Allows(night, NotificationChannel.Email, NotificationPriority.Normal, At(23)));
        Assert.False(NotificationPreference.Allows(night, NotificationChannel.Email, NotificationPriority.Normal, At(5)));
        Assert.True(NotificationPreference.Allows(night, NotificationChannel.Email, NotificationPriority.Normal, At(6)));
        Assert.True(NotificationPreference.Allows(night, NotificationChannel.Email, NotificationPriority.Urgent, At(23)));
        var lunch = Pref(from: 12, to: 13);
        Assert.False(NotificationPreference.Allows(lunch, NotificationChannel.Email, NotificationPriority.Normal, At(12)));
        Assert.True(NotificationPreference.Allows(lunch, NotificationChannel.Email, NotificationPriority.Normal, At(13)));
    }
}
