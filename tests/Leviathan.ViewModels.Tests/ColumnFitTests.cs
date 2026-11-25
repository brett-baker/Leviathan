using Leviathan.ViewModels.Folders;

namespace Leviathan.ViewModels.Tests;

public sealed class ColumnFitTests
{
    private static ColumnLayout Layout() =>
        new(["name", "size", "modified", "type"], new Dictionary<string, double> { ["name"] = 380, ["size"] = 100, ["modified"] = 160, ["type"] = 130 });

    private static double Width(ColumnLayout layout, string id) => layout.Slots.Single(s => s.Column.Id == id).Width;

    [Fact]
    public void FitTo_AWidthSmallerThanTheColumns_ShrinksThemToFit()
    {
        var layout = Layout();

        layout.FitTo(600);

        Assert.InRange(layout.TotalWidth, 595, 600);
        Assert.All(layout.Slots, s => Assert.True(s.Width >= ColumnLayout.MinFitWidth(s.Column)));
    }

    [Fact]
    public void FitTo_TooManyColumnsForTheWidth_StopsAtTheMinimumsAndLeavesTheRestToScrolling()
    {
        var layout = Layout();

        layout.FitTo(100);

        Assert.Equal(160, Width(layout, "name"));
        Assert.Equal(72, Width(layout, "size"));
        Assert.Equal(120, Width(layout, "modified"));
        Assert.Equal(72, Width(layout, "type"));
        Assert.True(layout.TotalWidth > 100);
    }

    [Fact]
    public void FitTo_AWiderPane_GivesTheSpareWidthToName()
    {
        var layout = Layout();

        layout.FitTo(1000);

        Assert.Equal(380 + 230, Width(layout, "name"));
        Assert.Equal(100, Width(layout, "size"));
        Assert.Equal(1000, layout.TotalWidth);
    }

    [Fact]
    public void FitTo_KeepsHiddenColumnsHidden()
    {
        var layout = Layout();

        layout.FitTo(500);

        Assert.False(layout.IsVisible("tags"));
        Assert.Equal(4, layout.Slots.Count);
    }

    [Fact]
    public void FitTo_ARowAlreadyNarrowerThanItsMinimum_IsNotWidened()
    {
        var layout = Layout();
        layout.Resize("size", 50);

        layout.FitTo(300);

        Assert.Equal(50, Width(layout, "size"));
    }

    [Fact]
    public void FitTo_RaisesWidthsChangedOnce_AndNothingWhenNothingChanges()
    {
        var layout = Layout();
        var raised = new List<bool>();
        layout.Changed += (_, structure) => raised.Add(structure);

        layout.FitTo(500);
        layout.FitTo(500);

        Assert.Equal([false], raised);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    public void FitTo_NoUsableWidth_ChangesNothing(double available)
    {
        var layout = Layout();

        layout.FitTo(available);

        Assert.Equal(770, layout.TotalWidth);
    }
}
