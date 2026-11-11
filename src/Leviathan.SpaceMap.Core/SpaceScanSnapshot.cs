using System.IO.Compression;
using System.Text;

namespace Leviathan.SpaceMap;

/// <summary>
/// A copy of a scan that can be saved and picked up again (spec §14 "Caching": the last scan of each location is saved with its date). A complete scan opens
/// instantly next time; a scan that was stopped, or killed with its worker, continues from the folders it had not listed yet.
/// </summary>
/// <param name="Root">The path scanned.</param>
/// <param name="ScannedAt">When the snapshot was taken.</param>
/// <param name="IsComplete">True when every folder was listed.</param>
/// <param name="Rows">The tree, parents first.</param>
public sealed record SpaceScanSnapshot(string Root, DateTimeOffset ScannedAt, bool IsComplete, IReadOnlyList<SpaceScanRow> Rows);

/// <summary>One node of a saved scan.</summary>
/// <param name="Parent">Index of the parent row, or -1 for the root.</param>
/// <param name="Name">Name.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="OwnSize">A file's size; 0 for folders (their totals are rebuilt from their files).</param>
/// <param name="ModifiedTicks">Last write time as UTC ticks (for a file; folders get the newest below them).</param>
/// <param name="Listed">For a folder: true when all of its entries are in the scan. A folder not listed is listed again on resume.</param>
/// <param name="Unreadable">True when the folder could not be read.</param>
public sealed record SpaceScanRow(int Parent, string Name, bool IsFolder, long OwnSize, long ModifiedTicks, bool Listed, bool Unreadable);

/// <summary>Where scans are kept between runs.</summary>
public interface ISpaceScanStore
{
    /// <summary>Replaces the saved scan for its root. Atomic.</summary>
    void Save(SpaceScanSnapshot snapshot);

    /// <summary>The saved scan of a root, or null when there is none or it cannot be read.</summary>
    SpaceScanSnapshot? Load(string root);

    /// <summary>Forgets the saved scan of a root.</summary>
    void Delete(string root);
}

/// <summary>Reads and writes a <see cref="SpaceScanSnapshot"/> as compressed bytes (a version byte first, so the format can change).</summary>
public static class SpaceScanCodec
{
    private const byte Version = 1;

    /// <summary>The bytes of a snapshot.</summary>
    public static byte[] Encode(SpaceScanSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var raw = new MemoryStream();
        using (var deflate = new DeflateStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
            writer.Write(snapshot.Root);
            writer.Write(snapshot.ScannedAt.UtcTicks);
            writer.Write(snapshot.IsComplete);
            writer.Write(snapshot.Rows.Count);
            foreach (var row in snapshot.Rows)
            {
                writer.Write(row.Parent);
                writer.Write(row.Name);
                writer.Write((byte)((row.IsFolder ? 1 : 0) | (row.Listed ? 2 : 0) | (row.Unreadable ? 4 : 0)));
                writer.Write(row.OwnSize);
                writer.Write(row.ModifiedTicks);
            }
        }

        return raw.ToArray();
    }

    /// <summary>The snapshot in <paramref name="bytes"/>, or null when they are not a snapshot this version can read.</summary>
    public static SpaceScanSnapshot? Decode(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using var raw = new MemoryStream(bytes);
            using var deflate = new DeflateStream(raw, CompressionMode.Decompress);
            using var reader = new BinaryReader(deflate, Encoding.UTF8);
            if (reader.ReadByte() != Version)
            {
                return null;
            }

            var root = reader.ReadString();
            var scannedAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var complete = reader.ReadBoolean();
            var count = reader.ReadInt32();
            if (count < 0 || count > 200_000_000)
            {
                return null;
            }

            var rows = new List<SpaceScanRow>(Math.Min(count, 1_000_000));
            for (var i = 0; i < count; i++)
            {
                var parent = reader.ReadInt32();
                var name = reader.ReadString();
                var flags = reader.ReadByte();
                var size = reader.ReadInt64();
                var modified = reader.ReadInt64();
                if (parent >= i)
                {
                    return null;
                }

                rows.Add(new SpaceScanRow(parent, name, (flags & 1) != 0, size, modified, (flags & 2) != 0, (flags & 4) != 0));
            }

            return new SpaceScanSnapshot(root, scannedAt, complete, rows);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or EndOfStreamException)
        {
            return null;
        }
    }
}
