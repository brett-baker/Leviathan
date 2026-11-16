using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Leviathan.UI.Tests;

/// <summary>
/// Dragging tabs and chips in the real strip, with the real mouse (spec §4.1; docs/uat P1-A step 13). The unit tests prove where the planner
/// puts a tab; these prove the window carries it out: the press, the moves and the release arrive, the tabs end up in the order the plan said,
/// a pinned tab cannot be picked up, and a clear drag away makes a window. Each test starts the app on a seeded session (tabs named after
/// their folders, in its own data folder) and reads the strip back through UI Automation.
/// </summary>
public sealed class TabDragTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "leviathan-ui-tabs-" + Guid.NewGuid().ToString("N"));
    private AppSession? _app;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private sealed record SeedTab(string Name, bool Pinned = false, Guid? Group = null);

    private static readonly Guid GroupId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Starts the app on a session with these tabs (a folder each) and, when asked, one group named <c>G</c>.</summary>
    private async Task StartAsync(bool withGroup, params SeedTab[] tabs)
    {
        var view = JsonSerializer.Deserialize<JsonElement>(
            """{"Layout":"Details","Sort":[{"Column":"name","Direction":"Ascending"}],"GroupBy":null,"Folders":"First","Columns":["name","size","modified"],"ColumnWidths":{"name":380,"size":100,"modified":160},"ThumbnailSize":128}""");
        var seeded = tabs.Select(t =>
        {
            var folder = Path.Combine(_root, t.Name);
            Directory.CreateDirectory(folder);
            return new
            {
                History = new { Back = Array.Empty<string>(), Current = folder, Forward = Array.Empty<string>() },
                View = view,
                CustomTitle = (string?)null,
                Accent = (string?)null,
                IsPinned = t.Pinned,
                IsLocked = false,
                IsSplit = false,
                Selection = Array.Empty<string>(),
                GroupId = t.Group,
                Split = (object?)null,
            };
        }).ToList();
        var session = new
        {
            SchemaVersion = 1,
            SavedAt = DateTimeOffset.UtcNow,
            State = new
            {
                Windows = new[]
                {
                    new
                    {
                        Bounds = new { Rect = new { X = 80, Y = 80, Width = 1500, Height = 900, Right = 1580, Bottom = 980 }, IsMaximized = false },
                        ShowPreview = false,
                        SelectedTab = 0,
                        Tabs = seeded,
                        SidebarWidth = 260,
                        SidebarCollapsed = false,
                        Groups = withGroup ? new[] { new { Id = GroupId, Name = "G", Color = "#3FA9F5", IsCollapsed = false } } : [],
                        Pane = new { Width = 417, VideoSize = (double?)null, VideoHeight = 334.5, PreviewCollapsed = false, DetailsCollapsed = false },
                    },
                },
                IsEmpty = false,
                TabCount = tabs.Length,
            },
        };
        _app = await AppSession.StartAsync(
            """{"SchemaVersion":1,"General":{"Startup":"LastSession","StartupProfile":null,"SingleClickToOpen":false,"NewTabLocation":""}}""",
            sessionJson: JsonSerializer.Serialize(session));
        Wait.Until(() => Strip().Count >= tabs.Length, TimeSpan.FromSeconds(20), "the seeded tabs");
    }

    /// <summary>The strip's items, left to right: the name UI Automation reports and where each stands on screen.</summary>
    private List<(string Name, Rectangle Bounds)> Strip() =>
        [.. _app!.Window.FindAllDescendants(cf => cf.ByControlType(ControlType.TabItem))
            .Select(e => (Name: e.Name ?? string.Empty, Bounds: e.BoundingRectangle))
            .Where(e => e.Bounds.Width > 0)
            .OrderBy(e => e.Bounds.Left)];

    /// <summary>The tabs' folder names in strip order (chips and the pinned mark in the name left out).</summary>
    private List<string> Order() =>
        [.. Strip().Select(e => e.Name).Where(n => !n.StartsWith("G,", StringComparison.Ordinal)).Select(n => n.Replace(", pinned", string.Empty, StringComparison.Ordinal))];

    private Rectangle Of(string name) => Strip().First(e => e.Name == name || e.Name == name + ", pinned").Bounds;

    private static Point Middle(Rectangle r) => new(r.Left + (r.Width / 2), r.Top + (r.Height / 2));

    private void AssertOrder(params string[] expected)
    {
        Wait.TryUntil(() => Order().SequenceEqual(expected), TimeSpan.FromSeconds(4));
        _app!.Screenshot("tab-drag");
        Assert.Equal(expected, Order());
    }

    [Fact]
    public async Task AnUnpinnedTab_DraggedRightOverTwoOthers_LandsAfterThem()
    {
        await StartAsync(false, new("tabA"), new("tabB"), new("tabC"), new("tabD"));
        var from = Middle(Of("tabA"));
        var c = Of("tabC");

        HandMouse.Drag(from, new Point(c.Right - 10, from.Y));

        AssertOrder("tabB", "tabC", "tabA", "tabD");
    }

    [Fact]
    public async Task AnUnpinnedTab_DraggedToTheFarLeft_LandsFirst()
    {
        await StartAsync(false, new("tabA"), new("tabB"), new("tabC"), new("tabD"));
        var from = Middle(Of("tabD"));
        var a = Of("tabA");

        HandMouse.Drag(from, new Point(a.Left + 5, from.Y));

        AssertOrder("tabD", "tabA", "tabB", "tabC");
    }

    [Fact]
    public async Task AnUnpinnedTab_LetGoAHandBelowTheStrip_IsStillAReorder()
    {
        await StartAsync(false, new("tabA"), new("tabB"), new("tabC"));
        var from = Middle(Of("tabA"));
        var c = Of("tabC");

        HandMouse.Drag(from, new Point(c.Right - 10, from.Y + 45));

        AssertOrder("tabB", "tabC", "tabA");
        Assert.Equal(1, _app!.WindowCount());
    }

    [Fact]
    public async Task ATab_DraggedClearlyAwayFromTheStrip_BecomesItsOwnWindow()
    {
        await StartAsync(false, new("tabA"), new("tabB"), new("tabC"));
        var from = Middle(Of("tabB"));

        HandMouse.Drag(from, new Point(from.X, from.Y + 300));

        Wait.Until(() => _app!.WindowCount() == 2, TimeSpan.FromSeconds(10), "the torn-off window");
    }

    [Fact]
    public async Task APinnedTab_CannotBePickedUp_AndOthersStayAfterIt()
    {
        await StartAsync(false, new("tabP", Pinned: true), new("tabA"), new("tabB"), new("tabC"));
        var pinned = Middle(Of("tabP"));
        var c = Of("tabC");

        HandMouse.Drag(pinned, new Point(c.Right - 10, pinned.Y));
        AssertOrder("tabP", "tabA", "tabB", "tabC");

        var last = Middle(Of("tabC"));
        var p = Of("tabP");
        HandMouse.Drag(last, new Point(p.Left + 3, last.Y));

        AssertOrder("tabP", "tabC", "tabA", "tabB");
    }

    [Fact]
    public async Task ATabOfAGroup_DraggedWithinIt_ReordersInsideTheGroup()
    {
        await StartAsync(true, new("tabA"), new("tabB", Group: GroupId), new("tabC", Group: GroupId), new("tabD"));
        var from = Middle(Of("tabB"));
        var c = Of("tabC");

        HandMouse.Drag(from, new Point(c.Right - 10, from.Y));

        AssertOrder("tabA", "tabC", "tabB", "tabD");
    }

    [Fact]
    public async Task AGroupChip_DraggedRightOverATab_MovesTheWholeGroup()
    {
        await StartAsync(true, new("tabA"), new("tabB", Group: GroupId), new("tabC", Group: GroupId), new("tabD"));
        var chip = Strip().First(e => e.Name.StartsWith("G,", StringComparison.Ordinal)).Bounds;
        var from = Middle(chip);
        var d = Of("tabD");

        HandMouse.Drag(from, new Point(d.Right - 10, from.Y));

        AssertOrder("tabA", "tabD", "tabB", "tabC");
    }
}
