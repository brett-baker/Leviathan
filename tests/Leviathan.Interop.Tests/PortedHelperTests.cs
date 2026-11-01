using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Leviathan.Interop.Tests;

public sealed unsafe class PortedHelperTests
{
    [Fact]
    public void ComHeapPtr_Dispose_FreesOnceAndNullsThePointer()
    {
        var memory = (int*)Marshal.AllocCoTaskMem(sizeof(int));
        var pointer = new ComHeapPtr<int>(memory);
        Assert.False(pointer.IsNull);
        Assert.True(pointer.Get() == memory);

        pointer.Dispose();
        pointer.Dispose();

        Assert.True(pointer.IsNull);
    }

    [Fact]
    public void ComHeapPtr_GetAddressOf_AllowsComOutParametersToFillIt()
    {
        var pointer = new ComHeapPtr<int>();
        var memory = (int*)Marshal.AllocCoTaskMem(sizeof(int));

        *pointer.GetAddressOf() = memory;

        Assert.True(pointer.Get() == memory);
        pointer.Dispose();
    }

    [Fact]
    public void ThrowIfFailedOnDebug_Success_ReturnsTheValue()
    {
        Assert.Equal(HRESULT.S_OK, HRESULT.S_OK.ThrowIfFailedOnDebug());
    }

    [Fact]
    public void ThrowIfFailedOnDebug_Failure_ThrowsInDebugOnly()
    {
#if DEBUG
        Assert.ThrowsAny<Exception>(() => HRESULT.E_FAIL.ThrowIfFailedOnDebug());
#else
        Assert.Equal(HRESULT.E_FAIL, HRESULT.E_FAIL.ThrowIfFailedOnDebug());
#endif
    }
}
