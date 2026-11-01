using Leviathan.Core.Paths;
using Leviathan.Core.Platform;

namespace Leviathan.Testing;

/// <summary>Known folders from a dictionary; folders not added are unavailable.</summary>
public sealed class FakeKnownFolders : IKnownFolders
{
    private readonly Dictionary<KnownFolder, LocationPath> _folders = [];

    /// <summary>Sets where <paramref name="folder"/> lives.</summary>
    public FakeKnownFolders With(KnownFolder folder, string path)
    {
        _folders[folder] = LocationPath.Parse(path);
        return this;
    }

    /// <inheritdoc />
    public LocationPath? Get(KnownFolder folder) => _folders.GetValueOrDefault(folder);
}

/// <summary>Drives from a list, optionally slow, to test the sidebar without touching real disks.</summary>
public sealed class FakeVolumeInfo : IVolumeInfo
{
    private readonly List<DriveDetails> _drives = [];

    /// <summary>Number of times the drives were listed.</summary>
    public int Calls { get; private set; }

    /// <summary>Adds a drive.</summary>
    public FakeVolumeInfo With(string root, string label, DriveKind kind, long total, long free, StorageMedium medium = StorageMedium.Ssd, string? network = null, bool responsive = true)
    {
        _drives.Add(new DriveDetails(LocationPath.Parse(root), label, kind, medium, kind == DriveKind.Network ? "NTFS" : "NTFS", total, free, network, responsive));
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DriveDetails>> GetDrivesAsync(CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DriveDetails>>([.. _drives]);
    }
}
