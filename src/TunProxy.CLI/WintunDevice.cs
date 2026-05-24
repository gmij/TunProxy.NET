using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using TunProxy.Core.Configuration;
using TunProxy.Core.Metrics;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// Windows Wintun 实现的 TUN 设备
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WintunDevice : ITunDevice
{
    private const string AdapterName = "TunProxy";
    private const string TunnelType = "Wintun";
    private static readonly Guid AdapterGuid = new("7D7F5B2D-6E4C-4C53-92E3-1F32C50DFE8B");
    private const int SendRingRetryCount = 200;
    private const int SendRingRetryDelayMilliseconds = 1;

    private WintunAdapter? _adapter;
    private WintunSession? _session;
    private bool _disposed;

    public WintunDevice(TunConfig config) { }

    public void Configure(string ip, string subnetMask, int mtu = 1500)
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address \"{AdapterName}\" static {ip} {subnetMask}",
            CreateNoWindow = true, UseShellExecute = false
        });
        p?.WaitForExit(3000);
    }

    public void Start()
    {
        _adapter = WintunAdapter.OpenOrCreateAdapter(AdapterName, TunnelType, AdapterGuid);
        _session = _adapter.StartSession(0x400000);
    }

    public void Stop()
    {
        _session?.Dispose();
        _session = null;
        _adapter?.Dispose();
        _adapter = null;
    }

    public byte[]? ReadPacket()
    {
        if (_session == null) return null;
        var readWaitEvent = _session.ReadWaitEvent;
        while (true)
        {
            var packet = _session.ReceivePacket(out var packetSize);
            if (packet != IntPtr.Zero)
            {
                try
                {
                    var data = new byte[packetSize];
                    Marshal.Copy(packet, data, 0, (int)packetSize);
                    return data;
                }
                finally { _session.ReleaseReceivePacket(packet); }
            }
            if (WintunNative.ERROR_NO_MORE_ITEMS == Marshal.GetLastWin32Error())
                WintunNative.WaitForSingleObject(readWaitEvent, 100);
            else
                return null;
        }
    }

    public void WritePacket(byte[] packet)
    {
        if (_session == null || packet.Length == 0) return;
        var exhaustedByOverflow = false;
        var ptr = AllocateSendPacketWithRetry(
            () => _session.AllocateSendPacket((uint)packet.Length),
            () => Marshal.GetLastWin32Error(),
            static () => Thread.Sleep(SendRingRetryDelayMilliseconds),
            SendRingRetryCount,
            TunDeviceWriteMetrics.IncrementSendAllocationRetryAttempts,
            () =>
            {
                exhaustedByOverflow = true;
                TunDeviceWriteMetrics.IncrementSendAllocationDrops();
            });
        if (ptr == IntPtr.Zero)
        {
            if (exhaustedByOverflow)
            {
                Log.Warning(
                    "[TUN ] Dropped packet because Wintun send ring stayed full after {Retries} retries.",
                    SendRingRetryCount);
            }
            return;
        }

        Marshal.Copy(packet, 0, ptr, packet.Length);
        _session.SendPacket(ptr);
    }

    internal static IntPtr AllocateSendPacketWithRetry(
        Func<IntPtr> allocatePacket,
        Func<int> getLastError,
        Action waitBeforeRetry,
        int maxRetries,
        Action? onRetryAttempt = null,
        Action? onRetryExhausted = null)
    {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            var pointer = allocatePacket();
            if (pointer != IntPtr.Zero)
            {
                return pointer;
            }

            if ((uint)getLastError() != WintunNative.ERROR_BUFFER_OVERFLOW)
            {
                return IntPtr.Zero;
            }

            if (attempt == maxRetries)
            {
                onRetryExhausted?.Invoke();
                return IntPtr.Zero;
            }

            onRetryAttempt?.Invoke();
            waitBeforeRetry();
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
