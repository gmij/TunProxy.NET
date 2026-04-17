using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using TunProxy.Core.Configuration;
using TunProxy.Core.Tun;
using TunProxy.Core.Wintun;

namespace TunProxy.CLI;

/// <summary>
/// Windows Wintun 实现的 TUN 设备
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WintunDevice : ITunDevice
{
    private WintunAdapter? _adapter;
    private WintunSession? _session;
    private bool _disposed;

    public WintunDevice(TunConfig config) { }

    public void Configure(string ip, string subnetMask, int mtu = 1500)
    {
        var p = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address \"TunProxy\" static {ip} {subnetMask}",
            CreateNoWindow = true, UseShellExecute = false
        });
        p?.WaitForExit(3000);
    }

    public void Start()
    {
        try
        {
            _adapter = WintunAdapter.OpenAdapter("TunProxy");
        }
        catch
        {
            _adapter = WintunAdapter.CreateAdapter("TunProxy", "Wintun");
        }
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
        var ptr = _session.AllocateSendPacket((uint)packet.Length);
        if (ptr == IntPtr.Zero) return;
        Marshal.Copy(packet, 0, ptr, packet.Length);
        _session.SendPacket(ptr);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
