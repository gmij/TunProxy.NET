using TunProxy.CLI;
using TunProxy.Core.Wintun;

namespace TunProxy.Tests;

public class WintunDeviceTests
{
    [Fact]
    public void AllocateSendPacketWithRetry_RetriesPastLegacyCapAndEventuallySucceeds()
    {
        const int overflowCount = 300; // intentionally greater than historical bounded retry cap (200)
        var attempts = 0;
        var waitCalls = 0;
        var retryCalls = 0;
        var pointer = new IntPtr(456);
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: () =>
            {
                attempts++;
                return attempts <= overflowCount ? IntPtr.Zero : pointer;
            },
            getLastError: () => (int)WintunNative.ERROR_BUFFER_OVERFLOW,
            waitBeforeRetry: () => waitCalls++,
            onRetryAttempt: () => retryCalls++);

        Assert.Equal(pointer, result);
        Assert.Equal(overflowCount, waitCalls);
        Assert.Equal(overflowCount, retryCalls);
    }

    [Fact]
    public void AllocateSendPacketWithRetry_ReturnsPointerAfterTransientOverflow()
    {
        var attempts = 0;
        var waitCalls = 0;
        var retryCalls = 0;
        var pointer = new IntPtr(123);
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: () =>
            {
                attempts++;
                return attempts < 3 ? IntPtr.Zero : pointer;
            },
            getLastError: () => (int)WintunNative.ERROR_BUFFER_OVERFLOW,
            waitBeforeRetry: () => waitCalls++,
            onRetryAttempt: () => retryCalls++);

        Assert.Equal(pointer, result);
        Assert.Equal(2, waitCalls);
        Assert.Equal(2, retryCalls);
    }

    [Fact]
    public void AllocateSendPacketWithRetry_ReturnsZeroWhenOverflowStopsBeingRetryable()
    {
        var waitCalls = 0;
        var retryCalls = 0;
        var errorReads = 0;
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: static () => IntPtr.Zero,
            getLastError: () =>
            {
                errorReads++;
                return errorReads <= 3
                    ? (int)WintunNative.ERROR_BUFFER_OVERFLOW
                    : 5;
            },
            waitBeforeRetry: () => waitCalls++,
            onRetryAttempt: () => retryCalls++);

        Assert.Equal(IntPtr.Zero, result);
        Assert.Equal(3, waitCalls);
        Assert.Equal(3, retryCalls);
    }

    [Fact]
    public void AllocateSendPacketWithRetry_StopsImmediatelyOnNonOverflowError()
    {
        var waitCalls = 0;
        var retryCalls = 0;
        var result = WintunDevice.AllocateSendPacketWithRetry(
            allocatePacket: static () => IntPtr.Zero,
            getLastError: static () => 5,
            waitBeforeRetry: () => waitCalls++,
            onRetryAttempt: () => retryCalls++);

        Assert.Equal(IntPtr.Zero, result);
        Assert.Equal(0, waitCalls);
        Assert.Equal(0, retryCalls);
    }
}
