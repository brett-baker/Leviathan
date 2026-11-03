using Leviathan.Core.Model;
using Microsoft.UI.Xaml;

namespace Leviathan.App;

/// <summary>A file view was right-clicked (or the menu key was pressed) and the window should show the right-click menu (spec §8.5).</summary>
/// <param name="Entry">The row under the pointer, or null for the empty space.</param>
/// <param name="Target">The element the position is relative to.</param>
/// <param name="Position">Where the menu should appear, in <paramref name="Target"/>'s coordinates.</param>
internal sealed record RowContextRequest(FileEntry? Entry, FrameworkElement Target, Windows.Foundation.Point Position);
