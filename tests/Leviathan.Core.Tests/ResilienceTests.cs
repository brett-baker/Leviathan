using Leviathan.Core.Paths;
using Leviathan.Core.Providers;
using Leviathan.Core.Resilience;
using Leviathan.Testing;

namespace Leviathan.Core.Tests;

public class BackoffPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(5, 32)]
    [InlineData(6, 60)]
    [InlineData(30, 60)]
    public void GetDelay_WithoutJitter_DoublesUpToMaximum(int attempt, int expectedSeconds)
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0);

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), policy.GetDelay(attempt, 0.5));
    }

    [Fact]
    public void GetDelay_WithJitter_StaysWithinBounds()
    {
        var policy = BackoffPolicy.Network;

        for (var attempt = 0; attempt < 10; attempt++)
        {
            foreach (var r in new[] { 0.0, 0.25, 0.5, 0.75, 0.999 })
            {
                var delay = policy.GetDelay(attempt, r);
                Assert.InRange(delay, TimeSpan.FromSeconds(0.8), policy.Maximum);
            }
        }
    }

    [Fact]
    public void Constructor_RejectsBadArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), 1.5));
    }
}

public class ShareHealthTrackerTests
{
    [Fact]
    public void FastSuccesses_AreOnline_SlowOnes_AreSlow()
    {
        var tracker = new ShareHealthTracker("nas", new ManualTimeProvider());

        tracker.RecordSuccess(TimeSpan.FromMilliseconds(12));
        Assert.Equal(ShareHealth.Online, tracker.Health);

        for (var i = 0; i < 10; i++)
        {
            tracker.RecordSuccess(TimeSpan.FromMilliseconds(900));
        }

        Assert.Equal(ShareHealth.Slow, tracker.Health);
    }

    [Fact]
    public void ConsecutiveFailures_MarkUnreachable_AndFailFastUntilBackoffElapses()
    {
        var time = new ManualTimeProvider();
        var tracker = new ShareHealthTracker("nas", time, new BackoffPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), 0));
        var changes = new List<ShareHealth>();
        tracker.HealthChanged += (_, h) => changes.Add(h);

        tracker.RecordFailure();
        Assert.True(tracker.ShouldAttempt());

        tracker.RecordFailure();
        Assert.Equal(ShareHealth.Unreachable, tracker.Health);
        Assert.False(tracker.ShouldAttempt());
        Assert.Equal(TimeSpan.FromSeconds(1), tracker.TimeUntilNextProbe());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(tracker.ShouldAttempt());

        tracker.RecordSuccess(TimeSpan.FromMilliseconds(10));
        Assert.Equal(ShareHealth.Online, tracker.Health);
        Assert.Equal(new[] { ShareHealth.Unreachable, ShareHealth.Online }, changes);
    }

    [Fact]
    public void AuthFailure_MarksAuthenticationRequired()
    {
        var tracker = new ShareHealthTracker("nas", new ManualTimeProvider());

        tracker.RecordFailure(authenticationRequired: true);

        Assert.Equal(ShareHealth.AuthenticationRequired, tracker.Health);
    }
}

public class LocationErrorsTests
{
    [Theory]
    [InlineData(64, LocationError.Unreachable)]
    [InlineData(53, LocationError.Unreachable)]
    [InlineData(121, LocationError.Timeout)]
    [InlineData(5, LocationError.AccessDenied)]
    [InlineData(3, LocationError.NotFound)]
    [InlineData(1326, LocationError.AuthenticationRequired)]
    [InlineData(1244, LocationError.AuthenticationRequired)]
    [InlineData(9999, LocationError.Unknown)]
    public void FromWin32_Classifies(int code, LocationError expected)
    {
        Assert.Equal(expected, LocationErrors.FromWin32(code));
    }

    [Fact]
    public void Wrap_IOExceptionWithNetNameDeleted_IsTransientUnreachable()
    {
        var location = LocationPath.Parse(@"\\nas\media");
        var io = new IOException("The specified network name is no longer available.", unchecked((int)0x80070040));

        var wrapped = LocationErrors.Wrap(io, location);

        Assert.Equal(LocationError.Unreachable, wrapped.Error);
        Assert.True(wrapped.IsTransient);
        Assert.Contains("nas", wrapped.Message);
        Assert.Same(io, wrapped.InnerException);
    }

    [Fact]
    public void Wrap_UnauthorizedAccess_IsAccessDenied_AndNotTransient()
    {
        var wrapped = LocationErrors.Wrap(new UnauthorizedAccessException(), LocationPath.Parse(@"C:\x"));

        Assert.Equal(LocationError.AccessDenied, wrapped.Error);
        Assert.False(wrapped.IsTransient);
    }

    [Fact]
    public void Win32Name_KnownAndUnknownCodes()
    {
        Assert.Equal("ERROR_NETNAME_DELETED", LocationErrors.Win32Name(64));
        Assert.Equal("WIN32_4242", LocationErrors.Win32Name(4242));
        Assert.Equal(64, LocationErrors.Win32CodeFromHResult(unchecked((int)0x80070040)));
    }
}

public class DeviceKeysTests
{
    [Theory]
    [InlineData(@"c:\users", "C:")]
    [InlineData(@"\\Diskstation\media\x", @"\\diskstation")]
    [InlineData("sftp://nas/volume1", "sftp://nas")]
    public void For_ReturnsDevice(string path, string expected)
    {
        Assert.Equal(expected, DeviceKeys.For(LocationPath.Parse(path)));
    }

    [Fact]
    public void ForJob_PrefersNetworkDevice()
    {
        var key = DeviceKeys.ForJob([LocationPath.Parse(@"D:\Exports"), LocationPath.Parse(@"\\nas\media\keep")]);

        Assert.Equal(@"\\nas", key);
        Assert.Null(DeviceKeys.ForJob([]));
    }
}
