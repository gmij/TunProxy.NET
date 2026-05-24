using TunProxy.CLI;
using TunProxy.Core.Wintun;

namespace TunProxy.Tests;

public class WintunDeviceTests
{
    [Fact]
    public void AllocateSendPacketWithRetry_ReturnsPointerAfterTransientOverflow()
    {
        var attempts = 0;
        var waitCalls = 0;
        var retryCalls = 0;
        var exhaustedCalls = 0;
        var pointer = new IntPtr(123);
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: () =>
            {
                attempts++;
                return attempts < 3 ? IntPtr.Zero : pointer;
            },
            getLastError: () => (int)WintunNative.ERROR_BUFFER_OVERFLOW,
            waitBeforeRetry: () => waitCalls++,
            maxRetries: 5,
            onRetryAttempt: () => retryCalls++,
            onRetryExhausted: () => exhaustedCalls++);

        Assert.Equal(pointer, result);
        Assert.Equal(2, waitCalls);
        Assert.Equal(2, retryCalls);
        Assert.Equal(0, exhaustedCalls);
    }

    [Fact]
    public void AllocateSendPacketWithRetry_ReturnsZeroWhenRetriesExhausted()
    {
        var waitCalls = 0;
        var retryCalls = 0;
        var exhaustedCalls = 0;
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: static () => IntPtr.Zero,
            getLastError: () => (int)WintunNative.ERROR_BUFFER_OVERFLOW,
            waitBeforeRetry: () => waitCalls++,
            maxRetries: 3,
            onRetryAttempt: () => retryCalls++,
            onRetryExhausted: () => exhaustedCalls++);

        Assert.Equal(IntPtr.Zero, result);
        Assert.Equal(3, waitCalls);
        Assert.Equal(3, retryCalls);
        Assert.Equal(1, exhaustedCalls);
    }

    [Fact]
    public void AllocateSendPacketWithRetry_StopsImmediatelyOnNonOverflowError()
    {
        var waitCalls = 0;
        var retryCalls = 0;
        var exhaustedCalls = 0;
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: static () => IntPtr.Zero,
            getLastError: static () => 5,
            waitBeforeRetry: () => waitCalls++,
            maxRetries: 5,
            onRetryAttempt: () => retryCalls++,
            onRetryExhausted: () => exhaustedCalls++);

        Assert.Equal(IntPtr.Zero, result);
        Assert.Equal(0, waitCalls);
        Assert.Equal(0, retryCalls);
        Assert.Equal(0, exhaustedCalls);
    }
}
