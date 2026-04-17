using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Serilog;
using TunProxy.Core.Tun;

namespace TunProxy.CLI;

/// <summary>
/// Linux /dev/net/tun 实现的 TUN 设备
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxTunDevice : ITunDevice
{
    private const int TUNSETIFF   = unchecked((int)0x400454CA);
    private const short IFF_TUN   = 0x0001;
    private const short IFF_NO_PI = 0x1000;

    private int _fd = -1;
    private readonly string _devName;
    private bool _disposed;

    public LinuxTunDevice(string devName = "tun0")
    {
        _devName = devName;
    }

    public void Configure(string ip, string subnetMask, int mtu = 1500)
    {
        var cidr = MaskToCidr(subnetMask);
        RunShell($"ip addr add {ip}/{cidr} dev {_devName}");
        RunShell($"ip link set {_devName} mtu {mtu} up");
    }

    public void Start()
    {
        _fd = LibcOpen("/dev/net/tun", 2 /* O_RDWR */, 0);
        if (_fd < 0)
            throw new InvalidOperationException($"open /dev/net/tun 失败：{Marshal.GetLastWin32Error()}");

        var ifreq = new byte[40];
        var nameBytes = Encoding.ASCII.GetBytes(_devName);
        Array.Copy(nameBytes, 0, ifreq, 0, Math.Min(nameBytes.Length, 15));
        BitConverter.GetBytes((short)(IFF_TUN | IFF_NO_PI)).CopyTo(ifreq, 16);

        int r;
        unsafe { fixed (byte* p = ifreq) r = LibcIoctl(_fd, TUNSETIFF, (nint)p); }
        if (r < 0)
            throw new InvalidOperationException($"ioctl TUNSETIFF 失败：{Marshal.GetLastWin32Error()}");

        Log.Information("Linux TUN 设备已打开：{Dev}", _devName);
    }

    public void Stop()
    {
        if (_fd >= 0) { LibcClose(_fd); _fd = -1; }
    }

    public unsafe byte[]? ReadPacket()
    {
        if (_fd < 0) return null;
        var buf = new byte[1500];
        fixed (byte* p = buf)
        {
            int n = LibcRead(_fd, p, buf.Length);
            if (n <= 0) return null;
            var data = new byte[n];
            Array.Copy(buf, data, n);
            return data;
        }
    }

    public unsafe void WritePacket(byte[] packet)
    {
        if (_fd < 0 || packet.Length == 0) return;
        fixed (byte* p = packet) LibcWrite(_fd, p, packet.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static int MaskToCidr(string mask)
    {
        if (!System.Net.IPAddress.TryParse(mask, out var addr)) return 24;
        int count = 0;
        foreach (var b in addr.GetAddressBytes())
        {
            var v = b; while (v != 0) { count += v & 1; v >>= 1; }
        }
        return count;
    }

    private static void RunShell(string cmd)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("sh", $"-c \"{cmd}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            p.WaitForExit(5000);
        }
        catch (Exception ex) { Log.Warning("Shell 命令失败 [{Cmd}]：{Msg}", cmd, ex.Message); }
    }

    [DllImport("libc", EntryPoint = "open",  SetLastError = true)]
    private static extern int LibcOpen([MarshalAs(UnmanagedType.LPStr)] string path, int flags, int mode);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LibcClose(int fd);
    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern unsafe int LibcIoctl(int fd, int request, nint arg);
    [DllImport("libc", EntryPoint = "read",  SetLastError = true)]
    private static extern unsafe int LibcRead(int fd, byte* buf, int count);
    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern unsafe int LibcWrite(int fd, byte* buf, int count);
}
