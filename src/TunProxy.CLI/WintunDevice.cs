using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
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
    private const int SendRingRetryDelayMilliseconds = 1;

    private WintunAdapter? _adapter;
    private WintunSession? _session;
    // Virtual DNS interceptor IP derived from the TUN adapter's own address.
    // By placing this IP inside the TUN subnet (e.g. 10.255.0.1 → 10.255.0.2),
    // this address is only reachable via the TUN interface's connected route.
    // All adapters (physical and TUN) are pointed at this IP so that every DNS
    // query – regardless of which interface Windows SMHNR picks – must travel
    // through TUN, where it is intercepted and forwarded via the upstream proxy.
    private readonly string _dnsInterceptorIp;
    // Saved DNS state for each physical adapter so it can be restored on stop.
    // Key = adapter Name, Value = (was DHCP, saved static DNS servers).
    private readonly Dictionary<string, (bool IsDhcp, string[] Addresses)> _savedAdapterDns = new();
    private bool _disposed;

    public WintunDevice(TunConfig config)
    {
        _dnsInterceptorIp = DeriveDnsInterceptorIp(config.IpAddress);
    }

    public void Configure(string ip, string subnetMask, int mtu = 1500)
    {
        var setAddress = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ip set address \"{AdapterName}\" static {ip} {subnetMask}",
            CreateNoWindow = true, UseShellExecute = false
        });
        setAddress?.WaitForExit(3000);

        var setMtu = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = $"interface ipv4 set subinterface \"{AdapterName}\" mtu={mtu} store=active",
            CreateNoWindow = true, UseShellExecute = false
        });
        setMtu?.WaitForExit(3000);

        ConfigureDnsServer();
    }

    public void Start()
    {
        _adapter = WintunAdapter.OpenOrCreateAdapter(AdapterName, TunnelType, AdapterGuid);
        _session = _adapter.StartSession(0x400000);
    }

    public void Stop()
    {
        ClearDnsServers();
        _session?.Dispose();
        _session = null;
        _adapter?.Dispose();
        _adapter = null;
    }

    private void ConfigureDnsServer()
    {
        Log.Information("[TUN ] DNS interceptor IP: {InterceptorIp}", _dnsInterceptorIp);

        // Step 1 – TUN adapter itself: point at the virtual interceptor IP with metric=1
        // so the TUN interface is the preferred name-resolution interface.
        RunNetsh($"interface ipv4 set dnsservers name=\"{AdapterName}\" static {_dnsInterceptorIp} primary validate=no");
        RunNetsh($"interface ipv4 set interface \"{AdapterName}\" metric=1");

        // Step 2 – Every active physical adapter: save current DNS and redirect it to
        // the same interceptor IP.  Because 10.255.0.0/24 has a connected route only
        // on the TUN interface, the OS routes DNS queries from *all* adapters through
        // TUN regardless of which adapter Windows SMHNR happens to pick.  This is
        // identical to what Clash / sing-box do on Windows.
        SaveAndRedirectPhysicalAdapterDns();

        // Step 3 – Flush the resolver cache so stale (possibly GFW-poisoned) answers
        // are discarded and all subsequent lookups go through TUN.
        FlushDnsCache();
    }

    private void ClearDnsServers()
    {
        RunNetsh($"interface ipv4 delete dnsservers name=\"{AdapterName}\" all");
        RestorePhysicalAdapterDns();
        FlushDnsCache();
    }

    // ── Physical-adapter DNS save / restore ───────────────────────────────────

    private void SaveAndRedirectPhysicalAdapterDns()
    {
        foreach (var adapter in GetPhysicalAdapters())
        {
            var props = adapter.GetIPProperties();
            var ipv4Props = props.GetIPv4Properties();
            var isDhcp = ipv4Props?.IsDhcpEnabled ?? false;
            var currentDns = props.DnsAddresses
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.ToString())
                .ToArray();

            _savedAdapterDns[adapter.Name] = (isDhcp, currentDns);
            Log.Information(
                "[TUN ] Redirecting DNS on \"{Adapter}\" to {InterceptorIp} (saved: {Saved})",
                adapter.Name,
                _dnsInterceptorIp,
                isDhcp ? "dhcp" : (currentDns.Length > 0 ? string.Join(", ", currentDns) : "none"));

            RunNetsh($"interface ipv4 set dnsservers name=\"{adapter.Name}\" static {_dnsInterceptorIp} primary validate=no");
        }
    }

    private void RestorePhysicalAdapterDns()
    {
        foreach (var (adapterName, (isDhcp, addresses)) in _savedAdapterDns)
        {
            if (isDhcp)
            {
                RunNetsh($"interface ipv4 set dnsservers name=\"{adapterName}\" dhcp");
                Log.Information("[TUN ] Restored DNS on \"{Adapter}\" → DHCP", adapterName);
            }
            else if (addresses.Length > 0)
            {
                RunNetsh($"interface ipv4 set dnsservers name=\"{adapterName}\" static {addresses[0]} primary validate=no");
                for (var i = 1; i < addresses.Length; i++)
                {
                    RunNetsh($"interface ipv4 add dnsservers name=\"{adapterName}\" {addresses[i]} index={i + 1} validate=no");
                }
                Log.Information("[TUN ] Restored DNS on \"{Adapter}\" → {DNS}", adapterName, string.Join(", ", addresses));
            }
            else
            {
                // No DNS was configured on this adapter – remove the override we added.
                RunNetsh($"interface ipv4 delete dnsservers name=\"{adapterName}\" all");
                Log.Information("[TUN ] Cleared DNS on \"{Adapter}\" (was empty)", adapterName);
            }
        }

        _savedAdapterDns.Clear();
    }

    /// <summary>
    /// Returns active physical IPv4 adapters – excludes loopback, tunnel, and the TUN
    /// adapter itself so we only touch real network interfaces.
    /// </summary>
    private static IEnumerable<NetworkInterface> GetPhysicalAdapters() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni =>
                ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                !ni.Name.Equals(AdapterName, StringComparison.OrdinalIgnoreCase) &&
                ni.GetIPProperties().UnicastAddresses
                    .Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork));

    // ── Shared helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Derives a virtual DNS interceptor IP from the TUN adapter's IP address by
    /// incrementing the last octet.  Example: 10.255.0.1 → 10.255.0.2.
    /// The resulting address lives inside the TUN subnet, so the OS connected
    /// route for that subnet forces all queries to it through the TUN interface.
    /// </summary>
    private static string DeriveDnsInterceptorIp(string tunIpAddress)
    {
        if (!IPAddress.TryParse(tunIpAddress, out var tunIp) ||
            tunIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return "10.255.0.2";
        }

        var bytes = tunIp.GetAddressBytes();
        bytes[3] = bytes[3] == 254 ? (byte)1 : (byte)(bytes[3] + 1);
        return new IPAddress(bytes).ToString();
    }

    private static void RunNetsh(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "netsh",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false
        });
        process?.WaitForExit(3000);
    }

    private static void FlushDnsCache()
    {
        var flush = Process.Start(new ProcessStartInfo
        {
            FileName = "ipconfig",
            Arguments = "/flushdns",
            CreateNoWindow = true,
            UseShellExecute = false
        });
        flush?.WaitForExit(3000);
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
        var ptr = AllocateSendPacketWithRetry(
            () => _session.AllocateSendPacket((uint)packet.Length),
            () => Marshal.GetLastWin32Error(),
            static () => Thread.Sleep(SendRingRetryDelayMilliseconds),
            TunDeviceWriteMetrics.IncrementSendAllocationRetryAttempts);
        if (ptr == IntPtr.Zero)
        {
            TunDeviceWriteMetrics.IncrementSendAllocationDrops();
            Log.Warning("[TUN ] Dropped packet because Wintun send packet allocation failed.");
            return;
        }

        Marshal.Copy(packet, 0, ptr, packet.Length);
        _session.SendPacket(ptr);
    }

    internal static IntPtr AllocateSendPacketWithRetry(
        Func<IntPtr> allocatePacket,
        Func<int> getLastError,
        Action waitBeforeRetry,
        Action? onRetryAttempt = null)
    {
        while (true)
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

            onRetryAttempt?.Invoke();
            waitBeforeRetry();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
