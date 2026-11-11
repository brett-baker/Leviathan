using Leviathan.Core.Paths;
using Leviathan.Core.Platform;
using Leviathan.Providers.Cloud;

namespace Leviathan.Providers.Cloud.Tests;

/// <summary>How each cloud sync client is doing (spec §7, Internet).</summary>
public sealed class CloudSyncProbeTests
{
    private static CloudProviderInfo Provider(CloudProviderKind kind, string folder) => new(kind, kind.ToString(), LocationPath.Parse(folder));

    private static HashSet<string> Running(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData(CloudProviderKind.OneDrive, "OneDrive")]
    [InlineData(CloudProviderKind.OneDriveCommercial, "onedrive")]
    [InlineData(CloudProviderKind.GoogleDrive, "GoogleDriveFS")]
    [InlineData(CloudProviderKind.Dropbox, "Dropbox")]
    [InlineData(CloudProviderKind.SynologyDrive, "cloud-drive-ui")]
    public void Decide_AClientWhoseProgramRuns_IsRunning(CloudProviderKind kind, string program) =>
        Assert.Equal(CloudSyncState.Running, CloudClients.Decide(kind, folderExists: true, Running(program, "explorer")));

    [Fact]
    public void Decide_AClientWhoseProgramIsNotThere_IsNotRunning() =>
        Assert.Equal(CloudSyncState.NotRunning, CloudClients.Decide(CloudProviderKind.Dropbox, true, Running("explorer", "OneDrive")));

    [Fact]
    public void Decide_AMissingFolder_WinsOverEverything() =>
        Assert.Equal(CloudSyncState.FolderMissing, CloudClients.Decide(CloudProviderKind.OneDrive, false, Running("OneDrive")));

    [Fact]
    public void Decide_AClientWithNoKnownProgram_IsUnknown() =>
        Assert.Equal(CloudSyncState.Unknown, CloudClients.Decide(CloudProviderKind.LucidLink, true, Running("explorer")));

    [Fact]
    public void Probe_ReadsTheRunningProgramsOnce_ForAllClients()
    {
        var reads = 0;
        var probe = new ProcessCloudSyncProbe(() =>
        {
            reads++;
            return Running("OneDrive", "Dropbox");
        }, folder => !folder.StartsWith(@"E:\", StringComparison.Ordinal));
        var one = Provider(CloudProviderKind.OneDrive, @"C:\Users\b\OneDrive");
        var box = Provider(CloudProviderKind.Box, @"C:\Users\b\Box");
        var gone = Provider(CloudProviderKind.Dropbox, @"E:\Dropbox");

        var states = probe.StatesOf([one, box, gone]);

        Assert.Equal(1, reads);
        Assert.Equal(CloudSyncState.Running, states[one]);
        Assert.Equal(CloudSyncState.NotRunning, states[box]);
        Assert.Equal(CloudSyncState.FolderMissing, states[gone]);
    }

    [Fact]
    public void Probe_WithNoClients_DoesNotLookAtThePrograms()
    {
        var probe = new ProcessCloudSyncProbe(() => throw new InvalidOperationException("must not be read"), _ => true);

        Assert.Empty(probe.StatesOf([]));
    }

    [Fact]
    public void Probe_AFolderThatCannotBeChecked_IsNotReportedMissing()
    {
        var probe = new ProcessCloudSyncProbe(() => Running("Dropbox"), _ => throw new UnauthorizedAccessException());
        var dropbox = Provider(CloudProviderKind.Dropbox, @"C:\Dropbox");

        Assert.Equal(CloudSyncState.Running, probe.StatesOf([dropbox])[dropbox]);
    }

    [Fact]
    public void RealProcessList_CanBeRead_AndTheCurrentProgramIsInIt()
    {
        var probe = new ProcessCloudSyncProbe();
        var self = Provider(CloudProviderKind.OneDrive, Path.GetTempPath());

        var states = probe.StatesOf([self]);

        Assert.True(states[self] is CloudSyncState.Running or CloudSyncState.NotRunning);
    }
}
