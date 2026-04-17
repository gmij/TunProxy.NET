using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;
using TunProxy.Core.Tun;

namespace TunProxy.CLI;

/// <summary>
/// macOS utun TUN 设备（通过 PF_SYSTEM/SYSPROTO_CONTROL 内核控制 socket）
/// </summary>
[SupportedOSPlatform("macos")]
public sealed unsafe class MacOsTunDevice : ITunDevice
{
    private const int PF_SYSTEM         = 32;
    private const int SOCK_DGRAM        = 2;
    private const int SYSPROTO_CONTROL  = 2;
    private const int AF_SYSTEM         = 32;
    private const int CTLIOCGINFO       = unchecked((int)0xC0644E03);
    private const int UTUN_OPT_IFNAME   = 2;
    private const string UTUN_CONTROL_NAME = "com.apple.net.utun_control";

    // IPv4 packet type prefix（utun 每包前有 4 字节协议族头，大端 AF_INET=2）
    private static readonly byte[] Ipv4Prefix = [0x00, 0x00, 0x00, 0x02];

    private int _fd = -1;
    private string _ifName = "utun0";
    private bool _disposed;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CtlInfo
    {
        public uint ctl_id;
        public fixed byte ctl_name[96];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SockaddrCtl
    {
        public byte   sc_len;
        public byte   sc_family;
        public ushort ss_sysaddr;
        public uint   sc_id;
        public uint   sc_unit;
        public ulong  sc_reserved;
    }

    public void Start()
    {
        _fd = LibcSocket(PF_SYSTEM, SOCK_DGRAM, SYSPROTO_CONTROL);
        if (_fd < 0)
            throw new InvalidOperationException($"socket(PF_SYSTEM) 失败：{Marshal.GetLastWin32Error()}");

        // 查询 utun 控制 ID
        var ctlInfo = new CtlInfo();
        var nameBytes = System.Text.Encoding.ASCII.GetBytes(UTUN_CONTROL_NAME);
        for (int i = 0; i < Math.Min(nameBytes.Length, 95); i++)
            ctlInfo.ctl_name[i] = nameBytes[i];

        if (LibcIoctl(_fd, CTLIOCGINFO, ref ctlInfo) < 0)
            throw new InvalidOperationException($"ioctl(CTLIOCGINFO) 失败：{Marshal.GetLastWin32Error()}");

        // 连接到 utun 控制
        var addr = new SockaddrCtl
        {
            sc_len     = (byte)sizeof(SockaddrCtl),
            sc_family  = (byte)AF_SYSTEM,
            ss_sysaddr = 2, // AF_SYS_CONTROL
            sc_id      = ctlInfo.ctl_id,
            sc_unit    = 0  // 内核自动分配单元号
        };

        if (LibcConnect(_fd, ref addr, sizeof(SockaddrCtl)) < 0)
            throw new InvalidOperationException($"connect(utun) 失败：{Marshal.GetLastWin32Error()}");

        // 读取实际接口名称（如 utun3）
        var nameBuf = new byte[16];
        uint nameLen = 16;
        fixed (byte* p = nameBuf)
            LibcGetsockopt(_fd, SYSPROTO_CONTROL, UTUN_OPT_IFNAME, p, ref nameLen);
        _ifName = System.Text.Encoding.ASCII.GetString(nameBuf, 0, (int)nameLen - 1).TrimEnd('\0');
        Log.Information("macOS utun 设备已打开：{Dev}", _ifName);
    }

    public void Configure(string ip, string subnetMask, int mtu = 1500)
    {
        RunShell($"ifconfig {_ifName} inet {ip} {ip} up mtu {mtu}");
        RunShell($"route add default -interface {_ifName}");
    }

    public void Stop()
    {
        if (_fd >= 0) { LibcClose(_fd); _fd = -1; }
    }

    public byte[]? ReadPacket()
    {
        if (_fd < 0) return null;
        var buf = new byte[4 + 1500]; // 4 字节协议族头 + 包体
        fixed (byte* p = buf)
        {
            int n = LibcRead(_fd, p, buf.Length);
            if (n <= 4) return null;
            var data = new byte[n - 4];
            Array.Copy(buf, 4, data, 0, data.Length);
            return data;
        }
    }

    public void WritePacket(byte[] packet)
    {
        if (_fd < 0 || packet.Length == 0) return;
        var buf = new byte[4 + packet.Length];
        Ipv4Prefix.CopyTo(buf, 0);
        Array.Copy(packet, 0, buf, 4, packet.Length);
        fixed (byte* p = buf) LibcWrite(_fd, p, buf.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    private static void RunShell(string cmd)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("sh", $"-c \"{cmd}\"")
                { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true })!;
            p.WaitForExit(5000);
        }
        catch (Exception ex) { Log.Warning("Shell 命令失败 [{Cmd}]：{Msg}", cmd, ex.Message); }
    }

    [DllImport("libSystem.dylib", EntryPoint = "socket",     SetLastError = true)]
    private static extern int LibcSocket(int domain, int type, int protocol);
    [DllImport("libSystem.dylib", EntryPoint = "ioctl",      SetLastError = true)]
    private static extern int LibcIoctl(int fd, int request, ref CtlInfo arg);
    [DllImport("libSystem.dylib", EntryPoint = "connect",    SetLastError = true)]
    private static extern int LibcConnect(int fd, ref SockaddrCtl addr, int addrlen);
    [DllImport("libSystem.dylib", EntryPoint = "getsockopt", SetLastError = true)]
    private static extern int LibcGetsockopt(int fd, int level, int optname, byte* optval, ref uint optlen);
    [DllImport("libSystem.dylib", EntryPoint = "close",      SetLastError = true)]
    private static extern int LibcClose(int fd);
    [DllImport("libSystem.dylib", EntryPoint = "read",       SetLastError = true)]
    private static extern int LibcRead(int fd, byte* buf, int count);
    [DllImport("libSystem.dylib", EntryPoint = "write",      SetLastError = true)]
    private static extern int LibcWrite(int fd, byte* buf, int count);
}
