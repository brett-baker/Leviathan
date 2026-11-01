using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>An entry of the placeholder sidebar list (replaced by the sidebar tree in slice D6).</summary>
/// <param name="Title">Main text.</param>
/// <param name="Detail">Second line: path or free space.</param>
/// <param name="Glyph">Segoe Fluent Icons glyph.</param>
/// <param name="Path">Where clicking goes.</param>
internal sealed record SidebarItem(string Title, string Detail, string Glyph, string Path);

/// <summary>Thin static wrappers so x:Bind in templates can call the tested formatting functions.</summary>
internal static class Formatters
{
    public static string Glyph(FileEntryKind kind, FileAttributes attributes) => kind switch
    {
        FileEntryKind.Folder => "",
        FileEntryKind.Link when attributes.HasFlag(FileAttributes.Directory) => "",
        FileEntryKind.Link => "",
        _ => "",
    };

    public static string SizeText(FileEntryKind kind, long size) => kind == FileEntryKind.File ? DisplayFormat.Size(size) : string.Empty;

    public static string DateText(DateTimeOffset modified) => DisplayFormat.Date(modified);

    public static Visibility VisibleWhenNotEmpty(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
