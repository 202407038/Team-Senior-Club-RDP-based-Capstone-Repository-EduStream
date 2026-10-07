using EduStream.Server;

namespace EduStream.FileTransfer.Tests;

public sealed class CaptureExclusionTests
{
    [Theory]
    [InlineData(0, true, 1400)]
    [InlineData(1, false, 50)]
    public void InvalidHandleOrUnsupportedWindowsDoesNotReportSuccess(int handle, bool supported, int expected)
    {
        Assert.False(CaptureExclusion.TryApply(new IntPtr(handle), supported,
            (_, _) => throw new Exception("Native call must not run"), () => 0, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void NativeFailurePreservesError()
    {
        Assert.False(CaptureExclusion.TryApply(new IntPtr(1), true, (_, _) => false, () => 5, out var error));
        Assert.Equal(5, error);
    }

    [Fact]
    public void NativeSuccessUsesExcludeFromCapture()
    {
        Assert.True(CaptureExclusion.TryApply(new IntPtr(1), true, (handle, affinity) =>
        {
            Assert.Equal(new IntPtr(1), handle); Assert.Equal(0x11u, affinity); return true;
        }, () => throw new Exception("Do not read stale error"), out var error));
        Assert.Equal(0, error);
    }
}
