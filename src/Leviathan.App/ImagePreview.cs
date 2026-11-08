using System.Runtime.InteropServices;
using Leviathan.ViewModels.Preview;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Leviathan.App;

/// <summary>
/// A picture in the preview pane (spec §9.1): decoded by Windows imaging (so JPEG, PNG, GIF, WebP, HEIC, AVIF and RAW work wherever
/// Windows has the codec), turned upright by its EXIF orientation, animated when the file is (GIF, WebP), with the wheel to zoom around the
/// pointer, a drag to pan, a double-click for 100% and back, and + / − / 0 on the keyboard. The file is opened off the UI thread, is refused
/// above <see cref="PreviewLimits.MaxImageBytes"/>, is given <see cref="PreviewLimits.LoadTimeout"/> to open, and is decoded no wider than
/// <see cref="PreviewLimits.MaxDecodeWidth"/>, so a huge picture or a slow share cannot stall the pane. The zoom arithmetic is
/// <see cref="ImageViewport"/>.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The open file is released by Clear, which every new picture and the window closing call; a control cannot express that as IDisposable.")]
internal sealed partial class ImagePreview : Grid
{
    private readonly ImageViewport _viewport = new();
    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
    };

    // The picture sits on a Canvas because a Grid would arrange it no larger than the pane and clip it to that before the zoom is applied.
    private readonly Canvas _canvas = new();
    private readonly CompositeTransform _transform = new();
    private readonly TextBlock _message = new()
    {
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(12),
        Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
        Visibility = Visibility.Collapsed,
    };

    private readonly Border _badge = new()
    {
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Bottom,
        Margin = new Thickness(6),
        Padding = new Thickness(6, 2, 6, 2),
        CornerRadius = new CornerRadius(3),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 0, 0, 0)),
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    private readonly TextBlock _badgeText = new() { FontSize = 11, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) };
    private FileStream? _file;
    private int _generation;
    private int _width;
    private int _height;
    private bool _dragging;
    private Windows.Foundation.Point _last;

    public ImagePreview()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
        IsTabStop = true;
        _image.RenderTransform = _transform;
        _badge.Child = _badgeText;
        _canvas.Children.Add(_image);
        Children.Add(_canvas);
        Children.Add(_message);
        Children.Add(_badge);
        SizeChanged += (_, e) =>
        {
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            _viewport.SetView(e.NewSize.Width, e.NewSize.Height);
            Apply();
        };
        PointerWheelChanged += OnWheel;
        PointerPressed += OnPressed;
        PointerMoved += OnMoved;
        PointerReleased += (_, e) => EndDrag(e.Pointer);
        PointerCaptureLost += (_, _) => _dragging = false;
        DoubleTapped += OnDoubleTapped;
        KeyDown += OnKeyDown;
    }

    /// <summary>Loads <paramref name="path"/>; throws <see cref="OperationCanceledException"/> when the selection moves on first. A file that cannot be shown leaves a message in the pane instead of throwing.</summary>
    public async Task OpenAsync(string path, CancellationToken cancellationToken)
    {
        Clear();
        var generation = _generation;
        FileStream? file = null;
        try
        {
            // Opening may block on a share, so it runs off the UI thread and is abandoned (and closed when it finally returns) on timeout.
            var open = Task.Run(() => OpenFile(path), CancellationToken.None);
            try
            {
                file = await open.WaitAsync(PreviewLimits.LoadTimeout, cancellationToken);
            }
            catch
            {
                _ = open.ContinueWith(t => t.Result.Dispose(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                throw;
            }

            var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken);
            var width = (int)decoder.OrientedPixelWidth;
            var height = (int)decoder.OrientedPixelHeight;
            stream.Seek(0);

            var bitmap = new BitmapImage();
            if (width > PreviewLimits.MaxDecodeWidth || height > PreviewLimits.MaxDecodeWidth)
            {
                if (width >= height)
                {
                    bitmap.DecodePixelWidth = PreviewLimits.MaxDecodeWidth;
                }
                else
                {
                    bitmap.DecodePixelHeight = PreviewLimits.MaxDecodeWidth;
                }
            }

            await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
            if (generation != _generation)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            _file = file;
            file = null;
            Show(bitmap, width, height);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Fail("This picture took too long to open.");
        }
        catch (InvalidDataException ex)
        {
            Fail(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or ArgumentException or NotSupportedException)
        {
            Fail(ex is IOException or UnauthorizedAccessException ? $"This picture could not be read: {ex.Message}" : "Windows has no decoder for this kind of picture.");
        }
        finally
        {
            file?.Dispose();
        }
    }

    /// <summary>Empties the pane and closes the file.</summary>
    public void Clear()
    {
        _generation++;
        _image.Source = null;
        _message.Visibility = Visibility.Collapsed;
        _badge.Visibility = Visibility.Collapsed;
        _width = _height = 0;
        _file?.Dispose();
        _file = null;
    }

    private static FileStream OpenFile(string path)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
        if (file.Length > PreviewLimits.MaxImageBytes)
        {
            var length = file.Length;
            file.Dispose();
            throw new InvalidDataException($"This picture is too large to preview ({length >> 20} MB).");
        }

        return file;
    }

    private void Show(ImageSource source, int width, int height)
    {
        _width = width;
        _height = height;
        _image.Width = width;
        _image.Height = height;
        _image.Source = source;
        _message.Visibility = Visibility.Collapsed;
        _viewport.SetView(ActualWidth, ActualHeight);
        _viewport.SetImage(width, height);
        Apply();
    }

    private void Fail(string text)
    {
        _image.Source = null;
        _badge.Visibility = Visibility.Collapsed;
        _message.Text = text;
        _message.Visibility = Visibility.Visible;
    }

    private void Apply()
    {
        if (_width == 0)
        {
            return;
        }

        _transform.ScaleX = _transform.ScaleY = _viewport.Scale;
        _transform.TranslateX = _viewport.OffsetX;
        _transform.TranslateY = _viewport.OffsetY;
        _badgeText.Text = $"{_width} × {_height} · {_viewport.Percent}%";
        _badge.Visibility = Visibility.Visible;
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_width == 0)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        _viewport.ZoomAt(Math.Pow(1.15, point.Properties.MouseWheelDelta / 120.0), point.Position.X, point.Position.Y);
        Apply();
        e.Handled = true;
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        var point = e.GetCurrentPoint(this);
        if (_width == 0 || !point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _last = point.Position;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void OnMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetCurrentPoint(this).Position;
        _viewport.PanBy(position.X - _last.X, position.Y - _last.Y);
        _last = position;
        Apply();
        e.Handled = true;
    }

    private void EndDrag(Pointer pointer)
    {
        if (_dragging)
        {
            _dragging = false;
            ReleasePointerCapture(pointer);
        }
    }

    private void OnDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_width == 0)
        {
            return;
        }

        var position = e.GetPosition(this);
        _viewport.ToggleActualSize(position.X, position.Y);
        Apply();
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_width == 0)
        {
            return;
        }

        var centre = (X: ActualWidth / 2, Y: ActualHeight / 2);
        switch ((int)e.Key)
        {
            case 187 or 107: // + and numpad +
                _viewport.ZoomAt(1.25, centre.X, centre.Y);
                break;
            case 189 or 109: // - and numpad -
                _viewport.ZoomAt(0.8, centre.X, centre.Y);
                break;
            case 48 or 96: // 0: fit
                _viewport.Fit();
                break;
            case 49 or 97: // 1: 100%
                _viewport.ToggleActualSize(centre.X, centre.Y);
                break;
            default:
                return;
        }

        Apply();
        e.Handled = true;
    }
}
