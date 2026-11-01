namespace Leviathan.ViewModels.Tests;

public sealed class AssemblyTests
{
    [Fact]
    public void ViewModelsAssembly_HasNoWinUiReference()
    {
        var references = typeof(Leviathan.ViewModels.AssemblyMarker).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "");

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.UI", StringComparison.Ordinal) || name.StartsWith("Microsoft.WinUI", StringComparison.Ordinal));
    }
}
