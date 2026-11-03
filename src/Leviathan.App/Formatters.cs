using Leviathan.Core.Model;
using Leviathan.ViewModels.Folders;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

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

    /// <summary>The icon-pack key that draws an entry until its own shell icon is shown (spec 4.3): folders, links and plain files.</summary>
    public static string IconKey(FileEntryKind kind, FileAttributes attributes) => kind switch
    {
        FileEntryKind.Folder => "folder",
        FileEntryKind.Link when attributes.HasFlag(FileAttributes.Directory) => "folder",
        FileEntryKind.Link => "link",
        _ => "file",
    };

    public static string SizeText(FileEntryKind kind, long size) => kind == FileEntryKind.File ? DisplayFormat.Size(size) : string.Empty;

    public static string DateText(DateTimeOffset modified) => DisplayFormat.Date(modified);

    public static Visibility VisibleWhenNotEmpty(string text) => string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
}
