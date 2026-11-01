using System.CommandLine;
using System.Globalization;
using System.IO.Abstractions;
using Leviathan.Diagnostics.Crash;

// leviathan-diag — reads Leviathan crash bundles.
//   leviathan-diag last            print the latest report.md
//   leviathan-diag list            crashes grouped by signature
//   leviathan-diag show <id>       print a specific report.md (ID or unique prefix)
//   leviathan-diag replay <id>     print the action trail to replay
//   leviathan-diag path            print the crashes folder
var dirOption = new Option<string?>("--dir")
{
    Description = "Crash bundle folder (default: %LOCALAPPDATA%\\Leviathan\\crashes).",
    Recursive = true,
};

CrashBundleStore Store(ParseResult result) => new(new FileSystem(), result.GetValue(dirOption) ?? CrashBundleStore.DefaultRoot);

var last = new Command("last", "Print the most recent report.md.");
last.SetAction(result =>
{
    var store = Store(result);
    if (store.Latest() is not { } bundle)
    {
        Console.Error.WriteLine($"No crash bundles in {store.Root}.");
        return 1;
    }

    Console.WriteLine(store.ReadMarkdown(bundle));
    return 0;
});

var list = new Command("list", "List crashes grouped by signature, most recent first.");
list.SetAction(result =>
{
    var groups = Store(result).Groups();
    if (groups.Count == 0)
    {
        Console.WriteLine("No crash bundles.");
        return 0;
    }

    Console.WriteLine($"{"Signature",-14} {"Count",5}  {"Last seen (UTC)",-17}  Latest bundle / summary");
    foreach (var group in groups)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{group.Signature,-14} {group.Count,5}  {group.LastSeen:yyyy-MM-dd HH:mm}  {group.Latest.Report.Id}"));
        Console.WriteLine($"{string.Empty,-40}{group.Latest.Report.Summary}");
    }

    return 0;
});

var idArgument = new Argument<string>("id") { Description = "Bundle ID or a unique prefix of it." };

var show = new Command("show", "Print a bundle's report.md.") { idArgument };
show.SetAction(result =>
{
    var store = Store(result);
    var id = result.GetValue(idArgument)!;
    if (store.Find(id) is not { } bundle)
    {
        Console.Error.WriteLine($"No unique bundle matches '{id}'.");
        return 1;
    }

    Console.WriteLine(store.ReadMarkdown(bundle));
    return 0;
});

var replay = new Command("replay", "Print the action trail of a bundle, oldest first.") { idArgument };
replay.SetAction(result =>
{
    var store = Store(result);
    var id = result.GetValue(idArgument)!;
    if (store.Find(id) is not { } bundle)
    {
        Console.Error.WriteLine($"No unique bundle matches '{id}'.");
        return 1;
    }

    if (bundle.Report.Actions.Count == 0)
    {
        Console.WriteLine("No actions were recorded for this bundle.");
        return 0;
    }

    foreach (var action in bundle.Report.Actions)
    {
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{action.Timestamp:HH:mm:ss.fff}  {action.Command,-16} {action.Target}"));
    }

    Console.WriteLine();
    Console.WriteLine("Automatic replay in a debug build arrives with the UI command layer (Phase 1).");
    return 0;
});

var path = new Command("path", "Print the crash bundle folder.");
path.SetAction(result =>
{
    Console.WriteLine(Store(result).Root);
    return 0;
});

var root = new RootCommand("Reads Leviathan crash bundles.") { last, list, show, replay, path };
root.Options.Add(dirOption);
return root.Parse(args).Invoke();
