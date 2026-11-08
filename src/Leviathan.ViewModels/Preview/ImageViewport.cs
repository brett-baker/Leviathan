namespace Leviathan.ViewModels.Preview;

/// <summary>
/// The zoom and pan of an image in the preview (spec §9.1: "zoom, pan"). It is only arithmetic, kept out of the control so it can be
/// tested: the picture starts fitted to the pane (never enlarged past 100%), the wheel zooms around the pointer, dragging pans, and the
/// picture can never be dragged off the pane. A pane that is resized while the picture is fitted keeps it fitted.
/// </summary>
public sealed class ImageViewport
{
    /// <summary>The most a picture is magnified (3200%): enough to see single pixels of a large photo.</summary>
    public const double MaxScale = 32;

    private double _imageWidth;
    private double _imageHeight;
    private double _viewWidth;
    private double _viewHeight;

    /// <summary>True while the picture follows the pane size; any zoom or pan clears it, <see cref="Fit"/> sets it.</summary>
    public bool IsFit { get; private set; } = true;

    /// <summary>Pixels of the screen per pixel of the picture.</summary>
    public double Scale { get; private set; } = 1;

    /// <summary>Where the left edge of the picture is, in pane pixels.</summary>
    public double OffsetX { get; private set; }

    /// <summary>Where the top edge of the picture is, in pane pixels.</summary>
    public double OffsetY { get; private set; }

    /// <summary>The scale that shows the whole picture; 1 when the picture is smaller than the pane.</summary>
    public double FitScale => _imageWidth <= 0 || _imageHeight <= 0 || _viewWidth <= 0 || _viewHeight <= 0
        ? 1
        : Math.Min(1, Math.Min(_viewWidth / _imageWidth, _viewHeight / _imageHeight));

    /// <summary>Zoom as a percentage for display ("100%").</summary>
    public int Percent => (int)Math.Round(Scale * 100);

    /// <summary>Sets the picture's size in its own pixels (after orientation) and fits it.</summary>
    public void SetImage(double width, double height)
    {
        _imageWidth = Math.Max(0, width);
        _imageHeight = Math.Max(0, height);
        Fit();
    }

    /// <summary>Sets the size of the area the picture is shown in.</summary>
    public void SetView(double width, double height)
    {
        _viewWidth = Math.Max(0, width);
        _viewHeight = Math.Max(0, height);
        if (IsFit)
        {
            Fit();
        }
        else
        {
            Clamp();
        }
    }

    /// <summary>Shows the whole picture, centred.</summary>
    public void Fit()
    {
        IsFit = true;
        Scale = FitScale;
        Clamp();
    }

    /// <summary>Multiplies the zoom by <paramref name="factor"/>, keeping the picture point under (<paramref name="x"/>, <paramref name="y"/>) still.</summary>
    public void ZoomAt(double factor, double x, double y) => SetScaleAt(Scale * factor, x, y);

    /// <summary>Shows the picture at 100% around the point, or fits it when it already is at 100%.</summary>
    public void ToggleActualSize(double x, double y)
    {
        if (!IsFit && Math.Abs(Scale - 1) < 0.001)
        {
            Fit();
        }
        else
        {
            SetScaleAt(1, x, y);
        }
    }

    /// <summary>Moves the picture by the distance, within the limits.</summary>
    public void PanBy(double dx, double dy)
    {
        IsFit = false;
        OffsetX += dx;
        OffsetY += dy;
        Clamp();
    }

    private void SetScaleAt(double scale, double x, double y)
    {
        if (_imageWidth <= 0 || _imageHeight <= 0)
        {
            return;
        }

        var target = Math.Clamp(scale, Math.Min(FitScale, 1), MaxScale);

        // The picture point under the pointer before the zoom must be under it after.
        var px = (x - OffsetX) / Scale;
        var py = (y - OffsetY) / Scale;
        Scale = target;
        OffsetX = x - (px * Scale);
        OffsetY = y - (py * Scale);
        IsFit = false;
        Clamp();
        if (Math.Abs(Scale - FitScale) < 1e-9)
        {
            IsFit = true;
        }
    }

    // A picture smaller than the pane is centred on that axis; a larger one cannot leave a gap at an edge.
    private void Clamp()
    {
        OffsetX = ClampAxis(OffsetX, _imageWidth * Scale, _viewWidth);
        OffsetY = ClampAxis(OffsetY, _imageHeight * Scale, _viewHeight);
    }

    private static double ClampAxis(double offset, double size, double view) =>
        size <= view ? (view - size) / 2 : Math.Clamp(offset, view - size, 0);
}
