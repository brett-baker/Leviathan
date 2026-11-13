using Leviathan.Contracts;
using Leviathan.Core.Paths;
using Leviathan.ViewModels.Archives;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Leviathan.App;

/// <summary>The dialogs of Extract to... and Compress with options (spec §15.4).</summary>
public sealed partial class MainWindow
{
    private static readonly string[] FormatChoices = ["Zip (opens everywhere)", "7z (smaller)"];
    private static readonly string[] LevelChoices = ["Store only", "Fastest", "Fast", "Normal", "Maximum", "Ultra"];

    /// <inheritdoc />
    public async Task<LocationPath?> PickFolderAsync(string title)
    {
        var tcs = new TaskCompletionSource<LocationPath?>();
        OnUi(async () =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FolderPicker();
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var folder = await picker.PickSingleFolderAsync();
                tcs.SetResult(folder is not null && LocationPath.TryParse(folder.Path, out var path) ? path : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                tcs.SetResult(null);
            }
        });
        return await tcs.Task;
    }

    /// <inheritdoc />
    public async Task<CompressChoice?> AskCompressAsync(string suggestedName)
    {
        var tcs = new TaskCompletionSource<CompressChoice?>();
        OnUi(async () =>
        {
            var name = new TextBox { Header = "Name", Text = suggestedName, MinWidth = 320 };
            var format = new ComboBox { Header = "Format", ItemsSource = FormatChoices, SelectedIndex = ArchiveNaming.FormatOf(suggestedName) == ArchiveFormat.SevenZip ? 1 : 0, MinWidth = 320 };
            var level = new ComboBox
            {
                Header = "Compression",
                ItemsSource = LevelChoices,
                SelectedIndex = (int)ArchiveLevel.Normal,
                MinWidth = 320,
            };
            var password = new PasswordBox { Header = "Password (optional)", MinWidth = 320 };
            var note = new TextBlock
            {
                Text = "A password uses AES-256. With 7z the list of files is hidden too.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
                MaxWidth = 320,
            };
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(name);
            panel.Children.Add(format);
            panel.Children.Add(level);
            panel.Children.Add(password);
            panel.Children.Add(note);
            var dialog = new ContentDialog
            {
                Title = "Compress",
                Content = panel,
                PrimaryButtonText = "Compress",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };

            // Changing the format moves the name's extension with it, so the name always says what will be made.
            format.SelectionChanged += (_, _) =>
            {
                var chosen = format.SelectedIndex == 1 ? ArchiveFormat.SevenZip : ArchiveFormat.Zip;
                name.Text = ArchiveNaming.WithExtension(name.Text, chosen);
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                tcs.SetResult(null);
                return;
            }

            var picked = format.SelectedIndex == 1 ? ArchiveFormat.SevenZip : ArchiveFormat.Zip;
            tcs.SetResult(new CompressChoice(ArchiveNaming.WithExtension(name.Text, picked), picked, (ArchiveLevel)Math.Max(0, level.SelectedIndex), string.IsNullOrEmpty(password.Password) ? null : password.Password));
        });
        return await tcs.Task;
    }
}
