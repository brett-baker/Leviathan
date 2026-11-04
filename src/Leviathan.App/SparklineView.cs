using Leviathan.ViewModels.Hub;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Leviathan.App;

/// <summary>
/// A speed sparkline (spec §12.6): the last samples of a job or of all jobs as a thin line, newest on the right. The points come
/// from <see cref="Sparkline"/>; this only hands them to a <see cref="Polyline"/>, so drawing costs one shape per line.
/// </summary>
internal sealed class SparklineView : Grid
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(SparklineView), new PropertyMetadata(null, (d, _) => ((SparklineView)d).Redraw()));

    private readonly Polyline _line = new()
    {
        StrokeThickness = 1.5,
        StrokeLineJoin = PenLineJoin.Round,
        Stroke = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
    };

    public SparklineView()
    {
        Width = 60;
        Height = 16;
        IsHitTestVisible = false;
        Children.Add(_line);
        SizeChanged += (_, _) => Redraw();
    }

    /// <summary>The samples, oldest first.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    private void Redraw()
    {
        var points = new PointCollection();
        foreach (var (x, y) in Sparkline.Points(Values ?? [], ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height))
        {
            points.Add(new Windows.Foundation.Point(x, y));
        }

        _line.Points = points;
        Visibility = points.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
}
