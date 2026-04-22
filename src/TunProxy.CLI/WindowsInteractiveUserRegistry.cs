using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace TunProxy.CLI;

[SupportedOSPlatform("windows")]
internal static class WindowsInteractiveUserRegistry
{
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TOKEN_INFORMATION_CLASS tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TOKEN_USER
    {
        public readonly SID_AND_ATTRIBUTES User;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SID_AND_ATTRIBUTES
    {
        public readonly IntPtr Sid;
        public readonly int Attributes;
    }

    public static bool TryGetInternetSettingsPath(out string settingsPath)
    {
        settingsPath = string.Empty;
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;

        try
        {
            var sessionId = WTSGetActiveConsoleSessionId();
            if (sessionId == 0xFFFFFFFF || !WTSQueryUserToken(sessionId, out token))
            {
                return false;
            }

            GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, IntPtr.Zero, 0, out var length);
            if (length <= 0)
            {
                return false;
            }

            buffer = Marshal.AllocHGlobal(length);
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenUser, buffer, length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var tokenUser = Marshal.PtrToStructure<TOKEN_USER>(buffer);
            var sid = new SecurityIdentifier(tokenUser.User.Sid);
            settingsPath = $@"{sid.Value}\Software\Microsoft\Windows\CurrentVersion\Internet Settings";
            return true;
        }
        catch
        {
            settingsPath = string.Empty;
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }

            if (token != IntPtr.Zero)
            {
                CloseHandle(token);
            }
        }
    }
}
