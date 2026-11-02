using Leviathan.ViewModels.Sidebar;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Leviathan.App;

/// <summary>Static helpers the sidebar template binds to (x:Bind functions): glyphs, indent, which parts of a row show.</summary>
internal static class SidebarFormat
{
    private static Brush? s_selected;
    private static Brush? s_warning;

    public static string Glyph(SidebarIcon icon) => icon switch
    {
        SidebarIcon.Home => "",
        SidebarIcon.Desktop => "",
        SidebarIcon.Documents => "",
        SidebarIcon.Downloads => "",
        SidebarIcon.Pictures => "",
        SidebarIcon.Videos => "",
        SidebarIcon.Music => "",
        SidebarIcon.Drive => "",
        SidebarIcon.Removable => "",
        SidebarIcon.NetworkDrive => "",
        SidebarIcon.Cloud => "",
        SidebarIcon.Favorite => "",
        _ => "",
    };

    public static string Chevron(bool expanded) => expanded ? "" : "";

    public static double IndentWidth(int depth) => Math.Max(0, depth - 1) * 14;

    public static Windows.UI.Text.FontWeight Weight(SidebarNodeKind kind) =>
        kind == SidebarNodeKind.Section ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public static Brush Foreground(SidebarNodeKind kind) => (Brush)Application.Current.Resources[
        kind is SidebarNodeKind.Section or SidebarNodeKind.Message or SidebarNodeKind.Loading ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"];

    public static Visibility HasDetail(SidebarNodeKind kind, string detail) => Show(kind == SidebarNodeKind.Place && detail.Length > 0);

    public static Visibility IsSection(SidebarNodeKind kind) => Show(kind == SidebarNodeKind.Section);

    public static Visibility IsItem(SidebarNodeKind kind) => Show(kind is SidebarNodeKind.Place or SidebarNodeKind.Folder);

    public static Visibility IsLoading(SidebarNodeKind kind) => Show(kind == SidebarNodeKind.Loading);

    public static Visibility IsMessage(SidebarNodeKind kind) => Show(kind == SidebarNodeKind.Message);

    public static Visibility NotSection(SidebarNodeKind kind) => Show(kind != SidebarNodeKind.Section);

    public static Visibility Visible(bool value) => Show(value);

    public static Visibility HasText(string text) => Show(!string.IsNullOrEmpty(text));

    public static Visibility HasBar(double? fraction) => Show(fraction is not null);

    public static double Percent(double? fraction) => (fraction ?? 0) * 100;

    public static Brush Background(bool selected) =>
        selected ? s_selected ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0x30, 0x60, 0x80, 0xC0)) : new SolidColorBrush(Colors.Transparent);

    public static Brush BarBrush(bool low) =>
        low ? s_warning ??= new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xD1, 0x34, 0x38)) : (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
