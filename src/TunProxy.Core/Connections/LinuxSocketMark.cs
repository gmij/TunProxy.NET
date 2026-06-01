using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace TunProxy.Core.Connections;

public static class LinuxSocketMark
{
    public const int TunProxyBypassMark = 0x5450;

    private const int SolSocket = 1;
    private const int SoMark = 36;

    public static bool TryApply(Socket socket, int? mark = TunProxyBypassMark)
    {
        if (mark is not { } value || !OperatingSystem.IsLinux())
        {
            return false;
        }

        var fd = socket.Handle.ToInt32();
        return setsockopt(fd, SolSocket, SoMark, ref value, sizeof(int)) == 0;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(
        int socket,
        int level,
        int optionName,
        ref int optionValue,
        int optionLength);
}
