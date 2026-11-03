using Leviathan.Engine.Supervision;

namespace Leviathan.ShellHost.Tests;

/// <summary>The connection's behaviour without a real process: nothing starts early, and a host that never answers fails fast.</summary>
public sealed class ShellHostConnectionTests
{
    private sealed class SilentProcess : ISupervisedProcess
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 1;

        public Task<int> Exited => _exited.Task;

        public void Kill() => _exited.TrySetResult(1);

        public void Dispose() => _exited.TrySetResult(0);
    }

    private sealed class CountingLauncher : IProcessLauncher
    {
        public int Starts { get; private set; }

        public ISupervisedProcess Start()
        {
            Starts++;
            return new SilentProcess();
        }
    }

    [Fact]
    public async Task Creating_TheConnection_StartsNothing()
    {
        var launcher = new CountingLauncher();

        await using var connection = new ShellHostConnection(launcher, "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, launcher.Starts);
        Assert.Equal(SupervisedState.Stopped, connection.State);
        Assert.False(connection.IsConnected);
    }

    [Fact]
    public async Task AHostThatNeverListens_FailsWithinTheLimit_WithAMessageAboutTheBuiltInItems()
    {
        var launcher = new CountingLauncher();
        await using var connection = new ShellHostConnection(launcher, "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N"));

        var ex = await Assert.ThrowsAsync<ShellHostUnavailableException>(() => connection.GetServiceAsync(TimeSpan.FromMilliseconds(600), TestContext.Current.CancellationToken));

        Assert.Contains("built-in", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, launcher.Starts);
    }

    [Fact]
    public async Task ACallerWhoCancels_GetsACancellation_NotAnUnavailableHost()
    {
        var launcher = new CountingLauncher();
        await using var connection = new ShellHostConnection(launcher, "Leviathan.Tests.ShellHost." + Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connection.GetServiceAsync(TimeSpan.FromSeconds(30), cancel.Token));
    }
}
