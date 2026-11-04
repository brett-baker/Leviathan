using Leviathan.ViewModels.Hub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace Leviathan.App;

/// <summary>
/// The activity strip (spec §12.6): a band of thin progress lines directly under the address bar, one per running job in the job's
/// colour, with room for four lines kept free even when nothing runs, so the window does not jump when a job starts. It only draws what <see cref="ActivityStripModel"/> says; click opens the Operations
/// panel (at the job, for a single line). Lines are kept and restyled rather than rebuilt, so a busy worker does not make the UI churn.
/// </summary>
public sealed partial class ActivityStrip : UserControl
{
    private readonly List<StripLineView> _views = [];

    /// <summary>Creates an empty strip.</summary>
    public ActivityStrip() => InitializeComponent();

    /// <summary>The line height in pixels, 1 or 2 (a setting in the spec).</summary>
    public double LineHeight { get; set; } = 2;

    /// <summary>Raised when a line (or the strip) is clicked; the id is the job of a single line, or null for the merged line.</summary>
    public event EventHandler<Guid?>? JobRequested;

    /// <summary>Draws these lines.</summary>
    public void Update(IReadOnlyList<StripLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        while (_views.Count > lines.Count)
        {
            var last = _views[^1];
            last.Stop();
            Lines.Children.Remove(last);
            _views.RemoveAt(_views.Count - 1);
        }

        while (_views.Count < lines.Count)
        {
            var view = new StripLineView(LineHeight);
            view.Tapped += (_, _) => JobRequested?.Invoke(this, view.JobId);
            _views.Add(view);
            Lines.Children.Add(view);
        }

        for (var i = 0; i < lines.Count; i++)
        {
            _views[i].Show(lines[i]);
        }
    }

    /// <summary>One line: a faint track, the filled part, and (when needed) the red tip.</summary>
    private sealed class StripLineView : Grid
    {
        private const double ShimmerWidth = 80;
        private readonly Border _track = new() { Opacity = 0.18 };
        private readonly Border _fill = new() { HorizontalAlignment = HorizontalAlignment.Left };
        private readonly Border _tip = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = 12, Visibility = Visibility.Collapsed };
        private readonly Border _shimmer = new() { HorizontalAlignment = HorizontalAlignment.Left, Width = ShimmerWidth, Visibility = Visibility.Collapsed, RenderTransform = new TranslateTransform() };
        private readonly Line _dashes = new() { StrokeDashArray = [3, 2], Visibility = Visibility.Collapsed };
        private StripLine? _line;
        private Storyboard? _animation;
        private StripLineStyle? _animatedStyle;

        public StripLineView(double height)
        {
            Height = height;
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            _dashes.Y1 = height / 2;
            _dashes.Y2 = height / 2;
            _dashes.StrokeThickness = height;
            Children.Add(_track);
            Children.Add(_fill);
            Children.Add(_shimmer);
            Children.Add(_dashes);
            Children.Add(_tip);
            SizeChanged += (_, _) =>
            {
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, ActualWidth, ActualHeight) };
                Layout();
                if (_animatedStyle == StripLineStyle.Shimmer)
                {
                    StartAnimation(StripLineStyle.Shimmer, force: true);
                }
            };
        }

        public Guid? JobId => _line?.JobId;

        public void Show(StripLine line)
        {
            _line = line;
            var color = ColorOf(JobPalette.Argb(line.ColorIndex));
            var brush = new SolidColorBrush(color);
            _track.Background = brush;
            _fill.Background = brush;
            _shimmer.Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 0 },
                    new GradientStop { Color = color, Offset = 0.5 },
                    new GradientStop { Color = Windows.UI.Color.FromArgb(0, color.R, color.G, color.B), Offset = 1 },
                },
            };
            _dashes.Stroke = brush;
            _tip.Background = new SolidColorBrush(ColorOf(JobPalette.Failure));
            _tip.Visibility = line.RedTip ? Visibility.Visible : Visibility.Collapsed;
            _shimmer.Visibility = line.Style == StripLineStyle.Shimmer ? Visibility.Visible : Visibility.Collapsed;
            _dashes.Visibility = line.Style == StripLineStyle.Dashed ? Visibility.Visible : Visibility.Collapsed;
            _fill.Visibility = line.Style is StripLineStyle.Solid or StripLineStyle.Pulse ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(this, line.MergedCount > 0 ? $"+{line.MergedCount}{Environment.NewLine}{line.Tooltip}" : line.Tooltip);
            Layout();
            StartAnimation(line.Style, force: false);
        }

        public void Stop()
        {
            _animation?.Stop();
            _animation = null;
            _animatedStyle = null;
        }

        private void Layout()
        {
            if (_line is not { } line || ActualWidth <= 0)
            {
                return;
            }

            var width = ActualWidth * line.Fraction;
            _fill.Width = width;
            _dashes.X1 = 0;
            _dashes.X2 = width;
            _tip.Margin = new Thickness(Math.Max(0, width - _tip.Width), 0, 0, 0);
        }

        // Starts the animation a style needs, unless it is already running (so a progress update does not restart the shimmer).
        private void StartAnimation(StripLineStyle style, bool force)
        {
            if (!force && _animatedStyle == style)
            {
                return;
            }

            Stop();
            _animatedStyle = style;
            Storyboard? storyboard = null;
            if (style == StripLineStyle.Pulse)
            {
                storyboard = new Storyboard();
                var pulse = new DoubleAnimation { From = 1, To = 0.3, Duration = TimeSpan.FromMilliseconds(900), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
                Storyboard.SetTarget(pulse, _fill);
                Storyboard.SetTargetProperty(pulse, "Opacity");
                storyboard.Children.Add(pulse);
            }
            else if (style == StripLineStyle.Shimmer && ActualWidth > 0)
            {
                storyboard = new Storyboard();
                var slide = new DoubleAnimation { From = -ShimmerWidth, To = ActualWidth, Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = RepeatBehavior.Forever };
                Storyboard.SetTarget(slide, _shimmer.RenderTransform);
                Storyboard.SetTargetProperty(slide, "X");
                storyboard.Children.Add(slide);
            }

            if (storyboard is not null)
            {
                _animation = storyboard;
                storyboard.Begin();
            }
            else
            {
                _fill.Opacity = 1;
            }
        }

        private static Windows.UI.Color ColorOf(uint argb) =>
            Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    }
}
