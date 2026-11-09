using Leviathan.ViewModels.Settings;

namespace Leviathan.ViewModels.Tests;

/// <summary>The tag settings besides the catalog (spec §11.1).</summary>
public sealed class TagSettingsTests
{
    [Fact]
    public void TintRows_IsOffByDefault()
    {
        Assert.False(new TagSettings().TintRows);
    }

    [Fact]
    public void Cleaned_KeepsTintRows()
    {
        var cleaned = (new TagSettings { TintRows = true, Items = [] }).Cleaned();

        Assert.True(cleaned.TintRows);
        Assert.NotEmpty(cleaned.Items);
    }
}
