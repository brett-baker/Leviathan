using System.Runtime.InteropServices.WindowsRuntime;
using Leviathan.Contracts;
using Leviathan.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// The contact sheet of one video in its own window (spec §10.4): a grid of frames taken evenly across it, drawn by the media
/// process. The grid size can be changed, "Save as…" exports the picture, and hovering a frame says where in the video it is from.
/// Playing from a frame arrives with the player.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The cancellation source is disposed when the window closes (Closed), which a Window cannot express as IDisposable.")]
internal sealed partial class ContactSheetWindow : Window
{
    private readonly FileEntry _video;
    private readonly ContactSheetClient _client;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _loading;
    private MediaContactSheetAnswer? _sheet;

    /// <summary>Opens the window and starts drawing the sheet of <paramref name="video"/>.</summary>
    public ContactSheetWindow(FileEntry video, ContactSheetClient client)
    {
        _video = video;
        _client = client;
        InitializeComponent();
        Title = $"Contact sheet · {video.Name}";
        TitleText.Text = video.Name;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
        _ = LoadAsync(4);
    }

    private async Task LoadAsync(int grid)
    {
        _loading?.Cancel();
        var loading = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _loading = loading;
        Busy.IsActive = true;
        Busy.Visibility = Visibility.Visible;
        ErrorText.Visibility = Visibility.Collapsed;
        SheetImage.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        _sheet = null;
        try
        {
            var answer = await _client.GetAsync(_video, grid, 240, loading.Token);
            if (loading.IsCancellationRequested)
            {
                return;
            }

            if (answer is { Status: MediaThumbnailStatus.Ok, Jpeg: { Length: > 0 } jpeg })
            {
                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(jpeg.AsBuffer());
                stream.Seek(0);
                var image = new BitmapImage();
                await image.SetSourceAsync(stream);
                SheetImage.Source = image;
                SheetImage.Visibility = Visibility.Visible;
                SaveButton.IsEnabled = true;
                _sheet = answer;
            }
            else
            {
                ErrorText.Text = answer.Status == MediaThumbnailStatus.ToolMissing
                    ? "ffmpeg was not found, so no contact sheet can be made. Set the ffmpeg folder in Settings."
                    : $"No contact sheet could be made of this file.{(string.IsNullOrWhiteSpace(answer.Message) ? string.Empty : "\n\n" + answer.Message.Trim())}";
                ErrorText.Visibility = Visibility.Visible;
            }
        }
        catch (OperationCanceledException)
        {
            // Another grid size was chosen, or the window closed.
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                Busy.IsActive = false;
                Busy.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void OnGridChanged(object sender, SelectionChangedEventArgs e)
    {
        // Setting the box up raises this once before the window is ready; only a change the user made reloads.
        if (e.RemovedItems.Count > 0 && GridBox.SelectedItem is ComboBoxItem { Tag: string tag } && int.TryParse(tag, out var grid))
        {
            _ = LoadAsync(grid);
        }
    }

    // The frame under the pointer, or -1 when there is no sheet yet.
    private int FrameAt(PointerRoutedEventArgs e)
    {
        if (_sheet is not { } sheet || sheet.FrameSeconds.Length == 0 || SheetImage.ActualWidth <= 0 || SheetImage.ActualHeight <= 0)
        {
            return -1;
        }

        var point = e.GetCurrentPoint(SheetImage).Position;
        var column = Math.Clamp((int)(point.X / SheetImage.ActualWidth * sheet.Columns), 0, sheet.Columns - 1);
        var row = Math.Clamp((int)(point.Y / SheetImage.ActualHeight * sheet.Rows), 0, sheet.Rows - 1);
        return Math.Min(row * sheet.Columns + column, sheet.FrameSeconds.Length - 1);
    }

    // A click plays the video from the moment that frame was taken at (spec §10.4).
    private void OnImagePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (FrameAt(e) is >= 0 and var index && _sheet is { } sheet)
        {
            new PlayerWindow(_video.Location.ToWin32Path(), _video.Name, TimeSpan.FromSeconds(sheet.FrameSeconds[index])).Activate();
        }
    }

    private void OnImagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (FrameAt(e) is not (>= 0 and var index) || _sheet is not { } sheet)
        {
            return;
        }

        HoverText.Text = $"Frame {index + 1} of {sheet.FrameSeconds.Length} · from {ViewModels.Folders.MediaColumns.Duration(TimeSpan.FromSeconds(sheet.FrameSeconds[index]))} into the video";
    }

    private void OnImagePointerExited(object sender, PointerRoutedEventArgs e) =>
        HoverText.Text = "Move the mouse over a frame to see where in the video it is from.";

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_sheet is not { Jpeg: { } jpeg })
        {
            return;
        }

        try
        {
            var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(_video.Name) + " - contact sheet" };
            picker.FileTypeChoices.Add("JPEG picture", [".jpg"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSaveFileAsync() is { } file)
            {
                await File.WriteAllBytesAsync(file.Path, jpeg);
                HoverText.Text = $"Saved {file.Name}";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            HoverText.Text = $"Could not save the picture: {ex.Message}";
        }
    }
}
