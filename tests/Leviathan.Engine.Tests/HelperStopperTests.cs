using Leviathan.Engine.Supervision;

namespace Leviathan.Engine.Tests;

public sealed class HelperStopperTests
{
    [Theory]
    [InlineData(@"C:\Users\b\AppData\Local\LeviathanApp\current\Leviathan.Worker.exe", @"C:\Users\b\AppData\Local\LeviathanApp\current\", true)]
    [InlineData(@"c:\users\b\appdata\local\leviathanapp\current\Leviathan.Worker.exe", @"C:\Users\b\AppData\Local\LeviathanApp\current", true)]
    [InlineData(@"C:\Users\b\AppData\Local\LeviathanApp\current\sub\Leviathan.Media.exe", @"C:\Users\b\AppData\Local\LeviathanApp\current", true)]
    [InlineData(@"D:\dev\Leviathan.Worker.exe", @"C:\Users\b\AppData\Local\LeviathanApp\current", false)]
    [InlineData(@"C:\Users\b\AppData\Local\LeviathanApp\current-old\Leviathan.Worker.exe", @"C:\Users\b\AppData\Local\LeviathanApp\current", false)]
    [InlineData("", @"C:\x", false)]
    [InlineData(@"C:\x\a.exe", "", false)]
    public void IsInside_OnlyFilesBelowTheProgramFolder(string file, string folder, bool expected) =>
        Assert.Equal(expected, HelperStopper.IsInside(file, folder));

    [Fact]
    public async Task StopAllAsync_WithNoHelperRunningFromTheFolder_StopsNothing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "leviathan-no-helpers-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(0, await HelperStopper.StopAllAsync(folder, CancellationToken.None));
    }
}
