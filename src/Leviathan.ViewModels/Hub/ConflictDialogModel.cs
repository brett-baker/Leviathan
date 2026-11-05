using System.Globalization;
using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Hub;

/// <summary>One choice in the conflict dialog's lists.</summary>
/// <param name="Policy">What the choice does.</param>
/// <param name="Label">Its words.</param>
public sealed record ConflictRuleOption(ConflictPolicy Policy, string Label)
{
    /// <summary>The rules a clash can be settled by (spec §12.4), in the order the dialog lists them.</summary>
    public static IReadOnlyList<ConflictRuleOption> All { get; } =
    [
        new(ConflictPolicy.Replace, "Replace"),
        new(ConflictPolicy.Skip, "Skip"),
        new(ConflictPolicy.KeepBoth, "Keep both"),
        new(ConflictPolicy.ReplaceIfNewer, "Replace if newer"),
        new(ConflictPolicy.ReplaceIfLarger, "Replace if larger"),
        new(ConflictPolicy.ReplaceIfDifferent, "Replace if different"),
    ];

    /// <summary>The words for <paramref name="policy"/>.</summary>
    public static string LabelOf(ConflictPolicy policy) => All.FirstOrDefault(o => o.Policy == policy)?.Label ?? policy.ToString();
}

/// <summary>One clash in the conflict dialog: both files in words, and the rule chosen for this file alone (null = follows the rule for all).</summary>
public sealed class ConflictRow
{
    /// <summary>Creates the row for <paramref name="item"/>.</summary>
    public ConflictRow(ConflictItem item, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        Item = item;
        Name = LocationPath.TryParse(item.Target, out var target) ? target.Name : item.Target;
        Folder = LocationPath.TryParse(item.Target, out var path) && path.Parent is { } parent ? parent.Value : string.Empty;
        NewText = Describe(item.SourceSize, item.SourceModified, zone);
        ExistingText = Describe(item.TargetSize, item.TargetModified, zone);
        Hint = HintOf(item);
    }

    /// <summary>The clash.</summary>
    public ConflictItem Item { get; }

    /// <summary>The file's name.</summary>
    public string Name { get; }

    /// <summary>The folder both files are named in at the destination.</summary>
    public string Folder { get; }

    /// <summary>The new file: "1.5 KB · 2025-06-01 00:00".</summary>
    public string NewText { get; }

    /// <summary>The file that is there now.</summary>
    public string ExistingText { get; }

    /// <summary>How they differ at a glance: "new is newer and larger", "same size and time".</summary>
    public string Hint { get; }

    /// <summary>The rule for this file alone, or null to follow the rule for all.</summary>
    public ConflictPolicy? Rule { get; set; }

    /// <summary>The words of the per-file list: "Same as above", then the rules.</summary>
    public static IReadOnlyList<string> RuleChoices { get; } = ["Same as above", .. ConflictRuleOption.All.Select(o => o.Label)];

    /// <summary>Position of <see cref="Rule"/> in <see cref="RuleChoices"/> (0 = follows the rule for all), for a list box bound to it.</summary>
    public int RuleIndex
    {
        get => Rule is { } rule ? ConflictRuleOption.All.ToList().FindIndex(o => o.Policy == rule) + 1 : 0;
        set => Rule = value > 0 && value <= ConflictRuleOption.All.Count ? ConflictRuleOption.All[value - 1].Policy : null;
    }

    private static string Describe(long size, DateTimeOffset modified, TimeZoneInfo? zone) => $"{DisplayFormat.Size(size)} · {DisplayFormat.Date(modified, zone)}";

    private static string HintOf(ConflictItem item)
    {
        var time = item.SourceModified.CompareTo(item.TargetModified) switch { > 0 => "newer", < 0 => "older", _ => "same time" };
        var size = item.SourceSize.CompareTo(item.TargetSize) switch { > 0 => "larger", < 0 => "smaller", _ => "same size" };
        return time == "same time" && size == "same size" ? "Same size and time" : $"New one is {time}, {size}";
    }
}

/// <summary>
/// The one conflict dialog of a job (spec §12.4): every name clash listed with both files, one rule for all of them and optional
/// rules for single files. The answer goes back to the job as a <see cref="ConflictAnswer"/>. The default rule is Keep both, because it
/// is the one that can never lose a file.
/// </summary>
public sealed class ConflictDialogModel
{
    /// <summary>Creates the model for the question <paramref name="conflicts"/>.</summary>
    public ConflictDialogModel(JobConflicts conflicts, TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        Conflicts = conflicts;
        Rows = [.. conflicts.Items.Select(i => new ConflictRow(i, zone))];
    }

    /// <summary>The question being answered.</summary>
    public JobConflicts Conflicts { get; }

    /// <summary>The clashes the dialog lists.</summary>
    public IReadOnlyList<ConflictRow> Rows { get; }

    /// <summary>The rule for every clash without one of its own.</summary>
    public ConflictPolicy ForAll { get; set; } = ConflictPolicy.KeepBoth;

    /// <summary>"3 files already exist in the destination." / "1 file already exists…".</summary>
    public string Title => Conflicts.TotalCount == 1
        ? "1 file already exists in the destination"
        : string.Create(CultureInfo.CurrentCulture, $"{Conflicts.TotalCount:N0} files already exist in the destination");

    /// <summary>Says which clashes are not listed, when there are more than the dialog was sent; empty otherwise.</summary>
    public string UnlistedText => Conflicts.TotalCount > Rows.Count
        ? string.Create(CultureInfo.CurrentCulture, $"{Conflicts.TotalCount - Rows.Count:N0} more are not listed; they follow \"{ConflictRuleOption.LabelOf(ForAll)}\".")
        : string.Empty;

    /// <summary>How many files have a rule of their own.</summary>
    public int OverrideCount => Rows.Count(r => r.Rule is not null);

    /// <summary>Sets the rule of every listed file to follow the rule for all again.</summary>
    public void ClearOverrides()
    {
        foreach (var row in Rows)
        {
            row.Rule = null;
        }
    }

    /// <summary>The answer to send: the rule for all, with the files that have their own.</summary>
    public ConflictAnswer ToAnswer()
    {
        var own = Rows.Where(r => r.Rule is { } rule && rule != ForAll && rule != ConflictPolicy.Ask)
            .ToDictionary(r => r.Item.Target, r => r.Rule!.Value, StringComparer.OrdinalIgnoreCase);
        return new ConflictAnswer(ForAll == ConflictPolicy.Ask ? ConflictPolicy.KeepBoth : ForAll, own.Count == 0 ? null : own);
    }
}
