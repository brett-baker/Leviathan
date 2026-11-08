using Leviathan.Core.Paths;
using Leviathan.Testing;
using Leviathan.ViewModels.Tabs;

namespace Leviathan.ViewModels.Tests;

public sealed class SlowClickDetectorTests
{
    private static readonly LocationPath A = LocationPath.Parse(@"C:\f\a.txt");
    private static readonly LocationPath B = LocationPath.Parse(@"C:\f\b.txt");

    private static (SlowClickDetector Detector, ManualTimeProvider Time) Make()
    {
        var time = new ManualTimeProvider();
        return (new SlowClickDetector(time, TimeSpan.FromMilliseconds(500)), time);
    }

    [Fact]
    public void FirstClick_NeverArms()
    {
        var (detector, _) = Make();

        Assert.False(detector.Press(A, wasOnlySelection: false, plain: true));
    }

    [Fact]
    public void SecondClick_OnTheSelectedItem_AfterTheDoubleClickTime_Arms()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);

        time.Advance(TimeSpan.FromMilliseconds(900));

        Assert.True(detector.Press(A, wasOnlySelection: true, plain: true));
    }

    [Fact]
    public void SecondClick_WithinTheDoubleClickTime_IsADoubleClick_AndDoesNotArm()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);

        time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.False(detector.Press(A, true, true));
    }

    [Fact]
    public void SecondClick_OnAnotherItem_DoesNotArm()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(detector.Press(B, true, true));
    }

    [Fact]
    public void SecondClick_WhenMoreThanOneItemIsSelected_DoesNotArm()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(detector.Press(A, wasOnlySelection: false, plain: true));
    }

    [Fact]
    public void SecondClick_WithCtrlOrShift_DoesNotArm()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.False(detector.Press(A, true, plain: false));
    }

    [Fact]
    public void Reset_ForgetsTheLastClick()
    {
        var (detector, time) = Make();
        detector.Press(A, false, true);
        time.Advance(TimeSpan.FromSeconds(2));
        detector.Reset();

        Assert.False(detector.Press(A, true, true));
    }
}
