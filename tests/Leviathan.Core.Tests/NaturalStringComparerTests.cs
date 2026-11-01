using Leviathan.Core.Text;

namespace Leviathan.Core.Tests;

public class NaturalStringComparerTests
{
    [Fact]
    public void Sort_OrdersDigitRunsByValue()
    {
        string[] names = ["file10.mp4", "file2.mp4", "File1.mp4", "file02.mp4", "file1a.mp4", "abc", "file"];

        var sorted = names.Order(NaturalStringComparer.Instance).ToArray();

        Assert.Equal(new[] { "abc", "file", "File1.mp4", "file1a.mp4", "file2.mp4", "file02.mp4", "file10.mp4" }, sorted);
    }

    [Theory]
    [InlineData("a", "B", -1)]
    [InlineData("x9", "x10", -1)]
    [InlineData("x10", "x9", 1)]
    [InlineData("2026-10-01", "2026-9-30", 1)]
    [InlineData("same", "same", 0)]
    public void Compare_ReturnsExpectedSign(string x, string y, int expectedSign)
    {
        Assert.Equal(expectedSign, Math.Sign(NaturalStringComparer.Instance.Compare(x, y)));
    }

    [Fact]
    public void Compare_HugeNumbers_DoNotOverflow()
    {
        var a = "v" + new string('9', 40);
        var b = "v1" + new string('0', 40);

        Assert.True(NaturalStringComparer.Instance.Compare(a, b) < 0);
    }

    [Fact]
    public void Compare_Nulls_SortFirst()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }

    [Fact]
    public void Compare_DifferentCaseOnly_IsStableButNotEqual()
    {
        var comparer = NaturalStringComparer.Instance;

        Assert.NotEqual(0, comparer.Compare("Readme", "README"));
        Assert.Equal(-comparer.Compare("Readme", "README"), comparer.Compare("README", "Readme"));
    }
}
