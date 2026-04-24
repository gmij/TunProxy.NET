using Microsoft.Win32;
using Serilog;
using TunProxy.Core;

namespace TunProxy.CLI;

internal static class WindowsServiceManager
{
    public static bool IsInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{TunProxyProduct.ServiceName}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool StartInstalledService()
    {
        var (exitCode, output) = RunSc($"start {TunProxyProduct.ServiceName}");
        if (exitCode == 0 || output.Contains("already", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        Log.Warning("Failed to start installed service. Output: {Output}", output.Trim());
        return false;
    }

    public static void Install(string exePath)
    {
        RunSc($"create {TunProxyProduct.ServiceName} binPath= \"{exePath}\" start= auto DisplayName= \"{TunProxyProduct.DisplayName}\"");
        RunSc($"description {TunProxyProduct.ServiceName} \"{TunProxyProduct.ServiceDescription}\"");
        RunSc($"sdset {TunProxyProduct.ServiceName} D:(A;;CCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;LCRPWPCR;;;IU)");
        RunSc($"failure {TunProxyProduct.ServiceName} reset= 60 actions= restart/1000");
        RunSc($"failureflag {TunProxyProduct.ServiceName} 1");
    }

    public static void Uninstall()
    {
        RunSc($"stop {TunProxyProduct.ServiceName}");
        Thread.Sleep(2000);
        RunSc($"delete {TunProxyProduct.ServiceName}");
    }

    public static (int ExitCode, string Output) RunSc(string args)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });
        if (process == null)
        {
            return (1, "Failed to start sc.exe.");
        }

        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return (1, output + "Command timed out.");
        }

        return (process.ExitCode, output);
    }
}
