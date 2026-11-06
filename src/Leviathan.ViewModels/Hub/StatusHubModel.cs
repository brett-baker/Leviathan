using CommunityToolkit.Mvvm.ComponentModel;

namespace Leviathan.ViewModels.Hub;

/// <summary>The tabs of the status hub (spec §16.1, D34), in the order they are shown.</summary>
public enum HubTab
{
    /// <summary>Running, queued and finished jobs (the Operations panel of §12.6).</summary>
    Operations,

    /// <summary>What the user did this session (D35).</summary>
    Activity,

    /// <summary>Background tasks: folder sizes, probing, indexing.</summary>
    Background,

    /// <summary>The Soft-delete bin: what was deleted from network shares, with Restore and Purge (spec §12.5).</summary>
    Deleted,

    /// <summary>The live knobs: limits, bandwidth, polling.</summary>
    QuickSettings,

    /// <summary>A compact treemap of the current folder.</summary>
    Space,
}

/// <summary>Where the status hub is shown (spec §16.1: fly-up by default, docked bottom or right, or its own window).</summary>
public enum HubPlacement
{
    /// <summary>Rises over the bottom of the file view and drops back when you click away, unless pinned.</summary>
    Flyout,

    /// <summary>A panel under the file view.</summary>
    DockBottom,

    /// <summary>A panel to the right of the file view.</summary>
    DockRight,

    /// <summary>Its own window. Not remembered between runs: the next run opens it as a fly-up.</summary>
    PoppedOut,
}

/// <summary>What is remembered about the status hub between runs: the tab last used, how it is shown, and its size.</summary>
public sealed record HubLayout
{
    /// <summary>The tab shown when the hub opens.</summary>
    public HubTab Tab { get; init; } = HubTab.Operations;

    /// <summary>How the hub is shown. A popped-out hub comes back as a fly-up.</summary>
    public HubPlacement Placement { get; init; } = HubPlacement.Flyout;

    /// <summary>True when the fly-up stays open until closed.</summary>
    public bool Pinned { get; init; }

    /// <summary>Height as a fraction of the window's body (fly-up and bottom dock), so it follows the window's size.</summary>
    public double HeightFraction { get; init; } = StatusHubModel.DefaultHeightFraction;

    /// <summary>Width in device-independent pixels (right dock).</summary>
    public double Width { get; init; } = StatusHubModel.DefaultWidth;
}

/// <summary>
/// The state of the status hub of one window (spec §16.1, D34): open or closed, the tab, how it is shown, pinned or not, and its
/// size. It decides what Esc and a click outside do, keeps the size inside sensible limits and tells whoever persists the layout
/// when it changes. No UI types, so every rule is unit-tested.
/// </summary>
public sealed partial class StatusHubModel : ObservableObject
{
    /// <summary>About 40% of the window body (spec §16.1).</summary>
    public const double DefaultHeightFraction = 0.4;

    /// <summary>The smallest fraction a drag can leave: a tab strip and a few rows.</summary>
    public const double MinHeightFraction = 0.15;

    /// <summary>The largest fraction: some of the file view always stays visible.</summary>
    public const double MaxHeightFraction = 0.8;

    /// <summary>Width of the right dock when nothing else was chosen.</summary>
    public const double DefaultWidth = 520;

    /// <summary>The narrowest right dock.</summary>
    public const double MinWidth = 280;

    /// <summary>The widest right dock as a fraction of the window's width.</summary>
    public const double MaxWidthFraction = 0.6;

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private HubTab _tab = HubTab.Operations;

    [ObservableProperty]
    private HubPlacement _placement = HubPlacement.Flyout;

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private double _heightFraction = DefaultHeightFraction;

    [ObservableProperty]
    private double _width = DefaultWidth;

    /// <summary>Raised when something that is remembered between runs changed.</summary>
    public event EventHandler<HubLayout>? LayoutChanged;

    /// <summary>What is remembered between runs.</summary>
    public HubLayout Layout => new()
    {
        Tab = Tab,
        Placement = Placement == HubPlacement.PoppedOut ? HubPlacement.Flyout : Placement,
        Pinned = IsPinned,
        HeightFraction = HeightFraction,
        Width = Width,
    };

    /// <summary>Takes a remembered layout (at startup). Does not open the hub unless it was docked, and does not raise <see cref="LayoutChanged"/>.</summary>
    public void Apply(HubLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _suppress = true;
        try
        {
            Tab = Enum.IsDefined(layout.Tab) ? layout.Tab : HubTab.Operations;
            Placement = Enum.IsDefined(layout.Placement) && layout.Placement != HubPlacement.PoppedOut ? layout.Placement : HubPlacement.Flyout;
            IsPinned = layout.Pinned;
            HeightFraction = ClampHeight(layout.HeightFraction);
            Width = Math.Max(MinWidth, double.IsFinite(layout.Width) ? layout.Width : DefaultWidth);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>Opens the hub on <paramref name="tab"/> (the tab last used when null).</summary>
    public void Open(HubTab? tab = null)
    {
        if (tab is { } wanted)
        {
            Tab = wanted;
        }

        IsOpen = true;
    }

    /// <summary>Closes the hub, pinned or not.</summary>
    public void Close() => IsOpen = false;

    /// <summary>
    /// The chevron, Ctrl+Shift+O and the status-bar parts: with a tab, opens that tab, or closes the hub when it already shows that
    /// tab; without one, opens or closes it on the tab last used.
    /// </summary>
    public void Toggle(HubTab? tab = null)
    {
        if (IsOpen && (tab is null || tab == Tab))
        {
            IsOpen = false;
            return;
        }

        Open(tab);
    }

    /// <summary>
    /// Esc or a click outside the hub. Only an unpinned fly-up closes: a pinned one, a docked one and a popped-out one stay.
    /// Returns true when the hub closed (the key or click was used).
    /// </summary>
    public bool Dismiss()
    {
        if (!IsOpen || IsPinned || Placement != HubPlacement.Flyout)
        {
            return false;
        }

        IsOpen = false;
        return true;
    }

    /// <summary>Sets the height from a drag: <paramref name="height"/> of <paramref name="available"/> pixels.</summary>
    public void SetHeight(double height, double available)
    {
        if (available > 0 && double.IsFinite(height))
        {
            HeightFraction = ClampHeight(height / available);
        }
    }

    /// <summary>Sets the width of the right dock from a drag, never wider than a share of <paramref name="available"/>.</summary>
    public void SetWidth(double width, double available)
    {
        if (!double.IsFinite(width))
        {
            return;
        }

        var max = available > 0 ? Math.Max(MinWidth, available * MaxWidthFraction) : double.MaxValue;
        Width = Math.Clamp(width, MinWidth, max);
    }

    /// <summary>Moves the hub to <paramref name="placement"/> (it stays open).</summary>
    public void Place(HubPlacement placement)
    {
        if (!Enum.IsDefined(placement))
        {
            return;
        }

        Placement = placement;
        IsOpen = true;
    }

    private static double ClampHeight(double fraction) =>
        double.IsFinite(fraction) ? Math.Clamp(fraction, MinHeightFraction, MaxHeightFraction) : DefaultHeightFraction;

    private bool _suppress;

    partial void OnTabChanged(HubTab value) => Persist();

    partial void OnPlacementChanged(HubPlacement value) => Persist();

    partial void OnIsPinnedChanged(bool value) => Persist();

    partial void OnHeightFractionChanged(double value) => Persist();

    partial void OnWidthChanged(double value) => Persist();

    private void Persist()
    {
        if (!_suppress)
        {
            LayoutChanged?.Invoke(this, Layout);
        }
    }
}
