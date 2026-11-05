using Leviathan.Contracts;
using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

/// <summary>The speed limit and schedule of Quick settings become what the worker takes (spec §12.3).</summary>
public sealed class BandwidthSettingsTests
{
    [Fact]
    public void ToBandwidth_ByDefault_IsUnlimited()
    {
        var bandwidth = new PerformanceSettings().ToBandwidth();

        Assert.Equal(0, bandwidth.Global.BytesPerSecond);
        Assert.Null(bandwidth.Global.Windows);
    }

    [Fact]
    public void ToBandwidth_CarriesTheLimitAndTheNightWindow()
    {
        var rule = new PerformanceSettings { BandwidthMBps = 50, ScheduleEnabled = true, ScheduleMBps = 0 }.ToBandwidth().Global;

        Assert.Equal(50_000_000, rule.BytesPerSecond);
        Assert.Equal(0, rule.LimitAt(new DateTime(2026, 1, 1, 23, 0, 0)));
        Assert.Equal(50_000_000, rule.LimitAt(new DateTime(2026, 1, 1, 12, 0, 0)));
    }

    [Fact]
    public void ToBandwidth_WithAScheduleOffOrEmpty_HasNoWindow()
    {
        Assert.Null(new PerformanceSettings { ScheduleEnabled = false }.ToBandwidth().Global.Windows);
        Assert.Null(new PerformanceSettings { ScheduleEnabled = true, ScheduleFrom = TimeSpan.FromHours(3), ScheduleTo = TimeSpan.FromHours(3) }.ToBandwidth().Global.Windows);
    }

    [Fact]
    public void Clamped_KeepsHandEditedValuesInRange()
    {
        var settings = new PerformanceSettings { BandwidthMBps = -5, ScheduleFrom = TimeSpan.FromDays(3), Verify = (VerifyMode)99 }.Clamped();

        Assert.Equal(0, settings.BandwidthMBps);
        Assert.Equal(TimeSpan.FromHours(22), settings.ScheduleFrom);
        Assert.Equal(VerifyMode.Default, settings.Verify);
    }
}
