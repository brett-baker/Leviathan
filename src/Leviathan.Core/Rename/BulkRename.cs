using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Leviathan.Core.Rename;

/// <summary>What a planned rename comes to.</summary>
public enum RenameStatus
{
    /// <summary>The name does not change.</summary>
    Unchanged,

    /// <summary>The new name is fine.</summary>
    Ok,

    /// <summary>The new name is already taken, by another item of the batch or by a file that is staying.</summary>
    Conflict,

    /// <summary>The new name cannot be used (empty, forbidden characters, a bad pattern or token).</summary>
    Invalid,
}

/// <summary>The outcome for one item.</summary>
/// <param name="Item">The item.</param>
/// <param name="OldName">Its name now.</param>
/// <param name="NewName">Its name after the operations (also shown when there is a problem, so it can be corrected).</param>
/// <param name="Status">Whether it can be applied.</param>
/// <param name="Problem">One sentence for a conflict or an invalid name.</param>
public sealed record RenameStep(RenameItem Item, string OldName, string NewName, RenameStatus Status, string? Problem);

/// <summary>One rename on disk.</summary>
/// <param name="From">Full path now.</param>
/// <param name="To">Full path after.</param>
public sealed record RenameMove(string From, string To);

/// <summary>The preview of a bulk rename: every item with its new name, and the safe order to do it in.</summary>
public sealed class RenamePlan
{
    internal RenamePlan(IReadOnlyList<RenameStep> steps, IReadOnlyDictionary<string, IReadOnlySet<string>> siblingsByFolder)
    {
        Steps = steps;
        _siblings = siblingsByFolder;
    }

    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _siblings;

    /// <summary>One entry per item, in the order given.</summary>
    public IReadOnlyList<RenameStep> Steps { get; }

    /// <summary>True when nothing is in conflict or invalid, so applying cannot collide or fail on a name.</summary>
    public bool CanApply => Steps.All(s => s.Status is RenameStatus.Ok or RenameStatus.Unchanged);

    /// <summary>Items whose name changes.</summary>
    public int ChangeCount => Steps.Count(s => s.Status == RenameStatus.Ok);

    /// <summary>
    /// The renames to perform, in an order that never lands on a name still in use: when a new name is the current name of another item
    /// that is also being renamed (a swap, or a shift such as 1→2, 2→3), that item first goes to a temporary name in its folder and
    /// reaches its real name last. Spec §15.3: a collision is never created silently.
    /// </summary>
    public IReadOnlyList<RenameMove> Moves()
    {
        if (!CanApply)
        {
            throw new InvalidOperationException("The plan has conflicts or invalid names; fix them before applying.");
        }

        var changed = Steps.Where(s => s.Status == RenameStatus.Ok).ToList();
        var oldNames = changed
            .GroupBy(s => Folder(s.Item), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(s => s.OldName).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

        var toTemporary = new List<RenameMove>();
        var direct = new List<RenameMove>();
        var fromTemporary = new List<RenameMove>();
        var n = 0;
        foreach (var step in changed)
        {
            var folder = Folder(step.Item);
            var from = step.Item.Path;
            var to = System.IO.Path.Combine(folder, step.NewName);
            var blocked = !string.Equals(step.OldName, step.NewName, StringComparison.OrdinalIgnoreCase) && oldNames[folder].Contains(step.NewName);
            if (!blocked)
            {
                direct.Add(new RenameMove(from, to));
                continue;
            }

            var taken = _siblings.TryGetValue(folder, out var set) ? set : new HashSet<string>();
            string temporary;
            do
            {
                temporary = System.IO.Path.Combine(folder, $"{step.OldName}.leviathan-rename-{++n}");
            }
            while (taken.Contains(System.IO.Path.GetFileName(temporary)));

            toTemporary.Add(new RenameMove(from, temporary));
            fromTemporary.Add(new RenameMove(temporary, to));
        }

        return [.. toTemporary, .. direct, .. fromTemporary];
    }

    /// <summary>The renames that put every name back: the moves in reverse order, each turned round.</summary>
    public static IReadOnlyList<RenameMove> Undo(IReadOnlyList<RenameMove> moves)
    {
        ArgumentNullException.ThrowIfNull(moves);
        return [.. moves.Reverse().Select(m => new RenameMove(m.To, m.From))];
    }

    private static string Folder(RenameItem item) => System.IO.Path.GetDirectoryName(item.Path) ?? string.Empty;
}

/// <summary>
/// The bulk-rename pipeline (spec §15.3): runs the operations in order over every item, checks the result for forbidden names and
/// collisions, and orders the renames so a batch can be applied and undone without ever overwriting or losing a name. Pure: nothing
/// here touches the disk, so the preview table can be rebuilt on every keystroke and the same plan can later run as a worker job.
/// </summary>
public static class BulkRename
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly Regex TokenPattern = new(@"\{(?<name>[A-Za-z]+)(?::(?<format>[^}]*))?\}", RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Plans the rename of <paramref name="items"/> (in the order that numbering follows) by <paramref name="operations"/>.
    /// <paramref name="siblingsOf"/> gives the names now in a folder, so a new name that is already taken by a file outside the batch is a
    /// conflict; without it only collisions inside the batch are found.
    /// </summary>
    public static RenamePlan Plan(IReadOnlyList<RenameItem> items, IReadOnlyList<RenameOperation> operations, Func<string, IReadOnlySet<string>>? siblingsOf = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(operations);
        var folders = items.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var siblings = folders.ToDictionary(f => f, f => siblingsOf?.Invoke(f) ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase);

        var names = new List<(string New, string? Problem)>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            names.Add(Apply(items[i], i, operations));
        }

        var steps = new RenameStep[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var old = NameOf(items[i]);
            var (name, problem) = names[i];
            problem ??= Check(name);
            steps[i] = problem is not null
                ? new RenameStep(items[i], old, name, RenameStatus.Invalid, problem)
                : new RenameStep(items[i], old, name, string.Equals(old, name, StringComparison.Ordinal) ? RenameStatus.Unchanged : RenameStatus.Ok, null);
        }

        // Final names must be unique per folder (names compare without regard to case, as on Windows).
        var holders = new Dictionary<(string, string), List<int>>();
        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Status is RenameStatus.Invalid)
            {
                continue;
            }

            holders.TryAdd((Folder(items[i]).ToUpperInvariant(), steps[i].NewName.ToUpperInvariant()), []);
            holders[(Folder(items[i]).ToUpperInvariant(), steps[i].NewName.ToUpperInvariant())].Add(i);
        }

        var vacated = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps.Where(s => s.Status == RenameStatus.Ok))
        {
            vacated.TryAdd(Folder(step.Item), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            vacated[Folder(step.Item)].Add(step.OldName);
        }

        for (var i = 0; i < steps.Length; i++)
        {
            if (steps[i].Status != RenameStatus.Ok)
            {
                continue;
            }

            var folder = Folder(items[i]);
            if (holders[(folder.ToUpperInvariant(), steps[i].NewName.ToUpperInvariant())].Count > 1)
            {
                steps[i] = steps[i] with { Status = RenameStatus.Conflict, Problem = $"Another item in this batch would also be called '{steps[i].NewName}'." };
            }
            else if (siblings[folder].Contains(steps[i].NewName)
                && !vacated[folder].Contains(steps[i].NewName)
                && !string.Equals(steps[i].OldName, steps[i].NewName, StringComparison.OrdinalIgnoreCase))
            {
                steps[i] = steps[i] with { Status = RenameStatus.Conflict, Problem = $"'{steps[i].NewName}' already exists in this folder." };
            }
        }

        return new RenamePlan(steps, siblings.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The problem with <paramref name="name"/> as a file or folder name on Windows, or null when it is usable.</summary>
    public static string? Check(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "The name would be empty.";
        }

        if (name.Length > 255)
        {
            return "The name would be longer than 255 characters.";
        }

        if (name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0 || name.Any(char.IsControl))
        {
            return "The name would contain a character Windows does not allow: < > : \" / \\ | ? *";
        }

        if (name.EndsWith('.') || name.EndsWith(' '))
        {
            return "The name would end with a dot or a space, which Windows does not allow.";
        }

        var stem = name.Contains('.', StringComparison.Ordinal) ? name[..name.IndexOf('.', StringComparison.Ordinal)] : name;
        return Reserved.Contains(stem.TrimEnd()) ? $"'{stem}' is a reserved device name on Windows." : null;
    }

    private static (string Name, string? Problem) Apply(RenameItem item, int index, IReadOnlyList<RenameOperation> operations)
    {
        var (stem, extension) = Split(System.IO.Path.GetFileName(item.Path), item.IsFolder);
        try
        {
            foreach (var operation in operations)
            {
                (stem, extension) = Step(operation, item, index, stem, extension);
            }
        }
        catch (RenameException ex)
        {
            return (Join(stem, extension), ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return (Join(stem, extension), "The pattern took too long on this name.");
        }
        catch (ArgumentException ex)
        {
            return (Join(stem, extension), "The pattern is not valid: " + ex.Message);
        }

        return (Join(stem, extension), null);
    }

    private static (string Stem, string Extension) Step(RenameOperation operation, RenameItem item, int index, string stem, string extension)
    {
        switch (operation)
        {
            case FindReplace f:
                return OnTarget(f.Target, stem, extension, item.IsFolder, text => Replace(f, text, item, index, stem, extension));
            case Insert i:
            {
                var text = Expand(i.Text, item, index, stem, extension);
                var at = Math.Clamp(i.FromEnd ? stem.Length - i.Position : i.Position, 0, stem.Length);
                return (stem.Insert(at, text), extension);
            }

            case RemoveCharacters r:
            {
                var start = Math.Clamp(r.FromEnd ? stem.Length - r.Start - Math.Max(0, r.Count) : r.Start, 0, stem.Length);
                var count = Math.Clamp(r.Count, 0, stem.Length - start);
                return (stem.Remove(start, count), extension);
            }

            case ChangeCase c:
                return OnTarget(c.Target, stem, extension, item.IsFolder, text => Case(text, c.Style));
            case TrimName t:
                return OnTarget(t.Target, stem, extension, item.IsFolder, text => Trim(text, t));
            case NumberSequence n:
            {
                var number = (n.Start + (long)n.Step * index).ToString("D" + Math.Clamp(n.Padding, 1, 18), CultureInfo.InvariantCulture);
                return (n.AtStart ? number + n.Separator + stem : stem + n.Separator + number, extension);
            }

            case NameList list:
                return index < list.Names.Count
                    ? Split(list.Names[index], item.IsFolder)
                    : throw new RenameException("The list has no name for this item.");

            case Template t:
            {
                var made = Expand(t.Pattern, item, index, stem, extension);
                if (extension.Length > 0 && made.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
                {
                    made = made[..^(extension.Length + 1)];
                }

                return (made, extension);
            }

            default:
                throw new RenameException("This kind of step is not known.");
        }
    }

    private static (string Stem, string Extension) OnTarget(RenameTarget target, string stem, string extension, bool isFolder, Func<string, string> change) => target switch
    {
        RenameTarget.Extension => (stem, isFolder ? extension : change(extension)),
        RenameTarget.Name => Split(change(Join(stem, extension)), isFolder),
        _ => (change(stem), extension),
    };

    private static string Replace(FindReplace f, string text, RenameItem item, int index, string stem, string extension)
    {
        if (f.Find.Length == 0)
        {
            return text;
        }

        if (f.UseRegex)
        {
            var options = RegexOptions.CultureInvariant | (f.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase);
            return Regex.Replace(text, f.Find, f.Replace, options, RegexTimeout);
        }

        return text.Replace(f.Find, Expand(f.Replace, item, index, stem, extension), f.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    private static string Case(string text, CaseStyle style)
    {
        switch (style)
        {
            case CaseStyle.Lower:
                return text.ToLowerInvariant();
            case CaseStyle.Upper:
                return text.ToUpperInvariant();
            case CaseStyle.Sentence:
                return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..].ToLowerInvariant();
            default:
            {
                var builder = new StringBuilder(text.Length);
                var startOfWord = true;
                foreach (var c in text)
                {
                    builder.Append(startOfWord ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                    startOfWord = c is ' ' or '_' or '-' or '.';
                }

                return builder.ToString();
            }
        }
    }

    private static string Trim(string text, TrimName t)
    {
        var trim = new List<char>(t.Extra) { ' ', '\t' };
        var result = text.Trim([.. trim]);
        return t.CollapseSpaces ? Regex.Replace(result, " {2,}", " ", RegexOptions.CultureInvariant, RegexTimeout) : result;
    }

    private static string Expand(string text, RenameItem item, int index, string stem, string extension) =>
        TokenPattern.Replace(text, match =>
        {
            var name = match.Groups["name"].Value.ToLowerInvariant();
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : null;
            switch (name)
            {
                case "name":
                    return stem;
                case "ext":
                    return extension;
                case "parent":
                    return System.IO.Path.GetFileName(Folder(item).TrimEnd('\\', '/'));
                case "date":
                    return Date(item.Modified, format);
                case "taken":
                    return item.Media?.Taken is { } taken ? Date(taken, format) : throw new RenameException("'" + System.IO.Path.GetFileName(item.Path) + "' has no date taken for {taken}.");
                case "duration":
                    return item.Media?.Duration is { } duration ? $"{(int)duration.TotalHours:00}-{duration.Minutes:00}-{duration.Seconds:00}" : throw new RenameException("There is no duration for {duration}.");
                case "resolution":
                    return item.Media is { Width: > 0, Height: > 0 } m ? $"{m.Width}x{m.Height}" : throw new RenameException("There is no resolution for {resolution}.");
                case "codec":
                    return !string.IsNullOrEmpty(item.Media?.Codec) ? item.Media.Codec : throw new RenameException("There is no codec for {codec}.");
                case "counter":
                    return Counter(index + 1, format);
                default:
                    throw new RenameException($"'{{{name}}}' is not a known token.");
            }
        });

    private static string Date(DateTime value, string? format)
    {
        try
        {
            return value.ToString(string.IsNullOrEmpty(format) ? "yyyy-MM-dd" : format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            throw new RenameException($"'{format}' is not a date format.");
        }
    }

    private static string Counter(long value, string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (format.All(c => c == '0'))
        {
            return value.ToString("D" + Math.Min(format.Length, 18), CultureInfo.InvariantCulture);
        }

        throw new RenameException($"'{format}' is not a counter format; use zeros such as {{counter:000}}.");
    }

    private static (string Stem, string Extension) Split(string name, bool isFolder)
    {
        var dot = name.LastIndexOf('.');
        return isFolder || dot <= 0 || dot == name.Length - 1 ? (name, string.Empty) : (name[..dot], name[(dot + 1)..]);
    }

    private static string Join(string stem, string extension) => extension.Length == 0 ? stem : stem + "." + extension;

    private static string NameOf(RenameItem item) => System.IO.Path.GetFileName(item.Path.TrimEnd('\\', '/'));

    private static string Folder(RenameItem item) => System.IO.Path.GetDirectoryName(item.Path.TrimEnd('\\', '/')) ?? string.Empty;

    private sealed class RenameException(string message) : Exception(message);
}
