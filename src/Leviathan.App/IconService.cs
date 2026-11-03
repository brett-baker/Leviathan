using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Leviathan.ViewModels.Icons;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace Leviathan.App;

/// <summary>
/// Draws the app's own icons from the chosen icon pack (spec §4.3, D33). Callers ask for a catalog key (or a Segoe glyph the
/// catalog knows) and get an element; the pack, the theme and high contrast decide what it looks like. Switching the pack
/// (Appearance setting) re-draws every icon that was bound with <see cref="Bind{T}"/>, no restart. Single-colour icons are
/// recoloured for their role and the theme; colour icons are drawn as they are; a key the pack lacks, or high contrast, gives the
/// system glyph. UI thread only.
/// </summary>
internal sealed class IconService
{
    private const string NeutralLight = "#242424";
    private const string NeutralDark = "#FFFFFF";
    private const double DisabledOpacity = 0.4;

    private readonly IconPacks _packs;
    private readonly Dictionary<(string Pack, string Key, string Color), byte[]> _bytes = [];
    private readonly List<Slot> _slots = [];
    private IconPack? _pack;
    private bool _dark;
    private bool _highContrast;

    /// <summary>Creates the service over the installed packs.</summary>
    public IconService(IconPacks packs, DispatcherQueue dispatcher)
    {
        ArgumentNullException.ThrowIfNull(packs);
        _packs = packs;
        _pack = packs.Resolve(null);
        try
        {
            var accessibility = new AccessibilitySettings();
            _highContrast = accessibility.HighContrast;
            accessibility.HighContrastChanged += (s, _) =>
            {
                var now = s.HighContrast;
                dispatcher.TryEnqueue(() =>
                {
                    _highContrast = now;
                    Redraw();
                });
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // No accessibility settings: icons are drawn as for normal contrast.
        }
    }

    /// <summary>Raised after the pack, the theme or high contrast changed and bound icons were redrawn.</summary>
    public event EventHandler? Changed;

    /// <summary>The installed packs.</summary>
    public IReadOnlyList<IconPack> Packs => _packs.All;

    /// <summary>The id of the pack in use, or empty when there are none (glyphs only).</summary>
    public string CurrentId => _pack?.Id ?? string.Empty;

    /// <summary>Switches to the pack with <paramref name="id"/> (an unknown id gives the default) and redraws bound icons.</summary>
    public void UsePack(string? id)
    {
        var pack = _packs.Resolve(id);
        if (ReferenceEquals(pack, _pack))
        {
            return;
        }

        _pack = pack;
        Redraw();
    }

    /// <summary>Tells the service whether the app is on a dark theme; neutral icons change colour with it.</summary>
    public void SetDark(bool dark)
    {
        if (_dark == dark)
        {
            return;
        }

        _dark = dark;
        Redraw();
    }

    /// <summary>The icon for <paramref name="key"/> at <paramref name="size"/> device-independent pixels.</summary>
    /// <param name="key">A key from <see cref="IconCatalog"/>.</param>
    /// <param name="size">Width and height.</param>
    /// <param name="tint">A colour <c>#RRGGBB</c> that replaces the role's colour (a user-chosen favourite colour, a tab colour); ignored by colour icons.</param>
    public IconElement Create(string key, double size = 16, string? tint = null)
    {
        var definition = IconCatalog.Find(key);
        if (definition is null)
        {
            return new FontIcon { Glyph = string.Empty, FontSize = size };
        }

        var packed = _highContrast ? null : _pack?.Get(key);
        if (packed is null)
        {
            return Glyph(definition, size, tint);
        }

        var color = tint ?? definition.Accent ?? (_dark ? NeutralDark : NeutralLight);
        var source = new SvgImageSource { RasterizePixelWidth = size * 3, RasterizePixelHeight = size * 3 };
        _ = LoadAsync(source, SvgBytes(_pack!.Id, key, packed, color));
        return new ImageIcon { Source = source, Width = size, Height = size };
    }

    /// <summary>The icon that stands for a Segoe glyph the catalog knows (menus and models that carry a glyph string), else the glyph itself.</summary>
    public IconElement CreateForGlyph(string glyph, double size = 16, string? tint = null) =>
        IconCatalog.FindByGlyph(glyph) is { } definition
            ? Create(definition.Key, size, tint)
            : new FontIcon { Glyph = glyph, FontSize = size * 0.875, Foreground = TintBrush(tint) };

    /// <summary>The same as <see cref="Create"/> for places that take an icon source (tab headers).</summary>
    public IconSource CreateSource(string key, double size = 16, string? tint = null)
    {
        var definition = IconCatalog.Find(key);
        var packed = _highContrast || definition is null ? null : _pack?.Get(key);
        if (definition is null || packed is null)
        {
            return new FontIconSource { Glyph = definition?.Glyph ?? string.Empty, FontSize = size * 0.875, Foreground = TintBrush(tint ?? (_highContrast ? null : definition?.Accent)) };
        }

        var color = tint ?? definition.Accent ?? (_dark ? NeutralDark : NeutralLight);
        var source = new SvgImageSource { RasterizePixelWidth = size * 3, RasterizePixelHeight = size * 3 };
        _ = LoadAsync(source, SvgBytes(_pack!.Id, key, packed, color));
        return new ImageIconSource { ImageSource = source };
    }

    /// <summary>
    /// Puts the icon on <paramref name="host"/> with <paramref name="set"/> now and again whenever the pack or theme changes. The
    /// service keeps only a weak reference to the host. An icon on a disabled host is dimmed (image icons do not follow the
    /// host's disabled colours by themselves).
    /// </summary>
    public void Bind<T>(T host, Action<T, IconElement> set, string key, double size = 16, string? tint = null)
        where T : Control
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(set);
        var slot = new Slot(new WeakReference<Control>(host), (h, i) => set((T)h, i), key, size, tint);
        _slots.Add(slot);
        host.IsEnabledChanged += (_, _) => slot.Dim(host.IsEnabled);
        Draw(slot, host);
    }

    private void Redraw()
    {
        _slots.RemoveAll(s => !s.Host.TryGetTarget(out _));
        foreach (var slot in _slots.ToList())
        {
            if (slot.Host.TryGetTarget(out var host))
            {
                Draw(slot, host);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Draw(Slot slot, Control host)
    {
        var icon = Create(slot.Key, slot.Size, slot.Tint);
        slot.Current = icon;
        slot.Dim(host.IsEnabled);
        slot.Set(host, icon);
    }

    private FontIcon Glyph(IconDefinition definition, double size, string? tint) =>
        new FontIcon { Glyph = definition.Glyph, FontSize = size * 0.875, Foreground = TintBrush(tint ?? (_highContrast ? null : definition.Accent)) };

    private static SolidColorBrush? TintBrush(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#'
            ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, Convert.ToByte(hex[1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)))
            : null;

    private byte[] SvgBytes(string pack, string key, PackedIcon packed, string color)
    {
        var cacheKey = (pack, key, packed.IsColor ? string.Empty : color);
        if (!_bytes.TryGetValue(cacheKey, out var bytes))
        {
            bytes = Encoding.UTF8.GetBytes(packed.IsColor ? packed.Svg : SvgTint.Apply(packed.Svg, color));
            _bytes[cacheKey] = bytes;
        }

        return bytes;
    }

    private static async Task LoadAsync(SvgImageSource source, byte[] svg)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(svg.AsBuffer());
            stream.Seek(0);
            await source.SetSourceAsync(stream);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // An SVG the renderer cannot read leaves the icon empty; the rest of the window is unaffected.
        }
    }

    private sealed class Slot(WeakReference<Control> host, Action<Control, IconElement> set, string key, double size, string? tint)
    {
        public WeakReference<Control> Host { get; } = host;

        public Action<Control, IconElement> Set { get; } = set;

        public string Key { get; } = key;

        public double Size { get; } = size;

        public string? Tint { get; } = tint;

        public IconElement? Current { get; set; }

        public void Dim(bool enabled)
        {
            if (Current is ImageIcon image)
            {
                image.Opacity = enabled ? 1 : DisabledOpacity;
            }
        }
    }
}

/// <summary>Short way to the icon service from anywhere in the UI: <c>Icons.Of("delete")</c>.</summary>
internal static class Icons
{
    private static IconService Service => App.Host.Icons;

    /// <inheritdoc cref="IconService.Create"/>
    public static IconElement Of(string key, double size = 16, string? tint = null) => Service.Create(key, size, tint);

    /// <inheritdoc cref="IconService.CreateForGlyph"/>
    public static IconElement OfGlyph(string glyph, double size = 16, string? tint = null) => Service.CreateForGlyph(glyph, size, tint);

    /// <inheritdoc cref="IconService.CreateSource"/>
    public static IconSource SourceOf(string key, double size = 16, string? tint = null) => Service.CreateSource(key, size, tint);

    /// <inheritdoc cref="IconService.Bind{T}"/>
    public static void Bind<T>(T host, Action<T, IconElement> set, string key, double size = 16, string? tint = null)
        where T : Control => Service.Bind(host, set, key, size, tint);
}
