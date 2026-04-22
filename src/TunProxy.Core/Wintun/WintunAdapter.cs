using System.Runtime.InteropServices;

namespace TunProxy.Core.Wintun;

/// <summary>
/// Wintun 适配器句柄
/// </summary>
public sealed class WintunAdapter : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    internal WintunAdapter(IntPtr handle)
    {
        _handle = handle;
    }

    public WintunSession StartSession(uint capacity = 0x400000)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunAdapter));

        var sessionHandle = WintunNative.WintunStartSession(_handle, capacity);
        if (sessionHandle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to start session: {Marshal.GetLastWin32Error()}");

        return new WintunSession(sessionHandle);
    }

    public void Dispose()
    {
        if (!_disposed && _handle != IntPtr.Zero)
        {
            WintunNative.WintunCloseAdapter(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }

    public static WintunAdapter CreateAdapter(string name, string tunnelType, Guid? requestedGuid = null)
    {
        var guid = requestedGuid ?? Guid.NewGuid();
        var handle = WintunNative.WintunCreateAdapter(name, tunnelType, ref guid);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to create adapter: {Marshal.GetLastWin32Error()}");

        return new WintunAdapter(handle);
    }

    public static WintunAdapter OpenOrCreateAdapter(string name, string tunnelType, Guid requestedGuid)
    {
        try
        {
            return OpenAdapter(name);
        }
        catch
        {
        }

        try
        {
            return CreateAdapter(name, tunnelType, requestedGuid);
        }
        catch
        {
            return OpenAdapter(name);
        }
    }

    public static WintunAdapter OpenAdapter(string name)
    {
        var handle = WintunNative.WintunOpenAdapter(name);
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to open adapter: {Marshal.GetLastWin32Error()}");

        return new WintunAdapter(handle);
    }
}

/// <summary>
/// Wintun 会话句柄
/// </summary>
public sealed class WintunSession : IDisposable
{
    private IntPtr _handle;
    private bool _disposed;

    internal WintunSession(IntPtr handle)
    {
        _handle = handle;
    }

    public IntPtr ReadWaitEvent => WintunNative.WintunGetReadWaitEvent(_handle);

    public unsafe IntPtr ReceivePacket(out uint packetSize)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunSession));

        return WintunNative.WintunReceivePacket(_handle, out packetSize);
    }

    public void ReleaseReceivePacket(IntPtr packet)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunSession));

        WintunNative.WintunReleaseReceivePacket(_handle, packet);
    }

    public unsafe IntPtr AllocateSendPacket(uint packetSize)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunSession));

        return WintunNative.WintunAllocateSendPacket(_handle, packetSize);
    }

    public void SendPacket(IntPtr packet)
    {
        if (_handle == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(WintunSession));

        WintunNative.WintunSendPacket(_handle, packet);
    }

    public void Dispose()
    {
        if (!_disposed && _handle != IntPtr.Zero)
        {
            WintunNative.WintunEndSession(_handle);
            _handle = IntPtr.Zero;
            _disposed = true;
        }
    }
}

/// <summary>
/// Wintun API 原生封装
/// </summary>
public static class WintunNative
{
    private const string DllName = "wintun";

    [DllImport(DllName, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr WintunCreateAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name,
        [MarshalAs(UnmanagedType.LPWStr)] string tunnelType,
        ref Guid requestedGuid);

    [DllImport(DllName, SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr WintunOpenAdapter(
        [MarshalAs(UnmanagedType.LPWStr)] string name);

    [DllImport(DllName, SetLastError = true)]
    public static extern void WintunCloseAdapter(IntPtr Adapter);

    [DllImport(DllName, SetLastError = true)]
    public static extern IntPtr WintunStartSession(IntPtr Adapter, uint capacity);

    [DllImport(DllName, SetLastError = true)]
    public static extern void WintunEndSession(IntPtr Session);

    [DllImport(DllName, SetLastError = true)]
    public static extern IntPtr WintunReceivePacket(IntPtr Session, out uint packetSize);

    [DllImport(DllName, SetLastError = true)]
    public static extern void WintunReleaseReceivePacket(IntPtr Session, IntPtr packet);

    [DllImport(DllName, SetLastError = true)]
    public static extern IntPtr WintunAllocateSendPacket(IntPtr Session, uint packetSize);

    [DllImport(DllName, SetLastError = true)]
    public static extern void WintunSendPacket(IntPtr Session, IntPtr packet);

    [DllImport(DllName, SetLastError = true)]
    public static extern IntPtr WintunGetReadWaitEvent(IntPtr Session);

    [DllImport(DllName, SetLastError = true)]
    public static extern bool WintunDeleteDriver();

    [DllImport(DllName, SetLastError = true)]
    public static extern uint WintunGetRunningDriverVersion();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    public const uint INFINITE = 0xFFFFFFFF;
    public const uint ERROR_NO_MORE_ITEMS = 259;
    public const uint ERROR_BUFFER_OVERFLOW = 111;
}
