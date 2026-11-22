using System.Globalization;
using System.Text.RegularExpressions;
using Leviathan.Diagnostics.Crash;

namespace Leviathan.ViewModels.Settings;

/// <summary>The update channel a build belongs to (release plan §2).</summary>
public enum ReleaseChannel
{
    /// <summary>A build from a developer's machine or CI, never released.</summary>
    Dev,

    /// <summary>A tagged <c>vX.Y.Z-beta.N</c> build.</summary>
    Beta,

    /// <summary>A tagged <c>vX.Y.Z</c> build.</summary>
    Stable,
}

/// <summary>
/// What this build says about itself: its version, commit and channel (release plan §1, §4). The version comes from the release
/// tag through the build (<c>-p:Version=</c>), so a build can never report a number its tag does not carry. A build without a
/// tag keeps the default <c>0.1.0</c> and is a <see cref="ReleaseChannel.Dev"/> build.
/// </summary>
/// <param name="Number">Major.minor.patch, e.g. <c>0.6.0</c>.</param>
/// <param name="Prerelease">The label after the dash, e.g. <c>beta.1</c>, or null.</param>
/// <param name="Commit">The commit the build was made from, or null when unknown.</param>
/// <param name="IsTagged">True when the version came from a release tag, false for a local build.</param>
public sealed partial record AppVersion(string Number, string? Prerelease, string? Commit, bool IsTagged)
{
    /// <summary>The version the repository builds as when no release tag set one.</summary>
    public const string UntaggedNumber = "0.1.0";

    /// <summary>The channel this build updates from: beta for <c>beta.N</c> labels, stable for none, dev when untagged.</summary>
    public ReleaseChannel Channel =>
        !IsTagged ? ReleaseChannel.Dev
        : Prerelease is null ? ReleaseChannel.Stable
        : ReleaseChannel.Beta;

    /// <summary>The version as shown to people: <c>0.6.0</c>, <c>0.6.0-beta.1</c> or <c>0.1.0 (development build)</c>.</summary>
    public string Display => Channel == ReleaseChannel.Dev ? $"{Number} (development build)" : Prerelease is null ? Number : $"{Number}-{Prerelease}";

    /// <summary>The first seven characters of the commit, or an empty string.</summary>
    public string ShortCommit => Commit is { Length: > 7 } c ? c[..7] : Commit ?? string.Empty;

    /// <summary>Reads the version from a build's informational version, e.g. <c>0.6.0-beta.1+3f2a9c1</c>.</summary>
    public static AppVersion Parse(BuildInfo build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var text = build.Version;
        var plus = text.IndexOf('+', StringComparison.Ordinal);
        var core = plus >= 0 ? text[..plus] : text;
        var match = CoreVersion().Match(core);
        if (!match.Success)
        {
            return new AppVersion(UntaggedNumber, null, build.Commit, false);
        }

        var number = match.Groups["number"].Value;
        var label = match.Groups["label"].Success ? match.Groups["label"].Value : null;
        return new AppVersion(number, label, build.Commit, IsTagged: number != UntaggedNumber || label is not null);
    }

    /// <summary>
    /// The version a release tag stands for: <c>v0.6.0</c> gives <c>0.6.0</c> and <c>v0.6.0-beta.2</c> gives <c>0.6.0-beta.2</c>.
    /// False for anything else, so the pipeline refuses a tag it cannot version (release plan §5).
    /// </summary>
    public static bool TryFromTag(string? tag, out string version)
    {
        var match = tag is null ? Match.Empty : ReleaseTag().Match(tag);
        version = match.Success ? match.Groups["version"].Value : string.Empty;
        return match.Success;
    }

    /// <summary>Compares two versions by SemVer precedence: a prerelease is older than its release, and <c>beta.10</c> is newer than <c>beta.2</c>.</summary>
    public static int Compare(string left, string right)
    {
        var (ln, ll) = Split(left);
        var (rn, rl) = Split(right);
        var byNumber = Version.Parse(ln).CompareTo(Version.Parse(rn));
        if (byNumber != 0)
        {
            return byNumber;
        }

        if (ll is null || rl is null)
        {
            return ll is null && rl is null ? 0 : ll is null ? 1 : -1;
        }

        var lp = ll.Split('.');
        var rp = rl.Split('.');
        for (var i = 0; i < Math.Min(lp.Length, rp.Length); i++)
        {
            var lNumeric = int.TryParse(lp[i], NumberStyles.None, CultureInfo.InvariantCulture, out var li);
            var rNumeric = int.TryParse(rp[i], NumberStyles.None, CultureInfo.InvariantCulture, out var ri);
            var c = lNumeric && rNumeric ? li.CompareTo(ri)
                : lNumeric ? -1
                : rNumeric ? 1
                : string.CompareOrdinal(lp[i], rp[i]);
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }

        return lp.Length.CompareTo(rp.Length);
    }

    private static (string Number, string? Label) Split(string version)
    {
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash < 0 ? (version, null) : (version[..dash], version[(dash + 1)..]);
    }

    [GeneratedRegex(@"^(?<number>\d+\.\d+\.\d+)(?:\.\d+)?(?:-(?<label>[0-9A-Za-z][0-9A-Za-z.\-]*))?$")]
    private static partial Regex CoreVersion();

    [GeneratedRegex(@"^v(?<version>\d+\.\d+\.\d+(?:-(?:beta)\.\d+)?)$")]
    private static partial Regex ReleaseTag();
}
