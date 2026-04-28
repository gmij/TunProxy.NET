using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32;

namespace TunProxy.Core.WindowsServices;

[SupportedOSPlatform("windows")]
public static class WindowsServiceManager
{
    private static readonly TimeSpan DefaultControlTimeout = TimeSpan.FromSeconds(20);

    public static bool IsInstalled(string serviceName = TunProxyProduct.ServiceName)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryGetStatus(
        out ServiceControllerStatus status,
        string serviceName = TunProxyProduct.ServiceName)
    {
        status = default;

        if (!IsInstalled(serviceName))
        {
            return false;
        }

        try
        {
            using var controller = new ServiceController(serviceName);
            status = controller.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsStopped(string serviceName = TunProxyProduct.ServiceName) =>
        TryGetStatus(out var status, serviceName) && status == ServiceControllerStatus.Stopped;

    public static bool StartInstalledService(
        string serviceName = TunProxyProduct.ServiceName,
        TimeSpan? timeout = null)
    {
        try
        {
            ControlInstalledServiceDirectly(
                WindowsServiceControlAction.Start,
                serviceName,
                timeout ?? DefaultControlTimeout);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void ControlInstalledService(
        WindowsServiceControlAction action,
        string serviceName = TunProxyProduct.ServiceName,
        TimeSpan? timeout = null,
        bool elevateOnAccessDenied = false)
    {
        var controlTimeout = timeout ?? DefaultControlTimeout;
        try
        {
            ControlInstalledServiceDirectly(action, serviceName, controlTimeout);
        }
        catch (Exception ex) when (elevateOnAccessDenied && IsAccessDenied(ex))
        {
            RunElevatedServiceControl(action, serviceName, controlTimeout);
        }
    }

    public static void ControlInstalledServiceDirectly(
        WindowsServiceControlAction action,
        string serviceName = TunProxyProduct.ServiceName,
        TimeSpan? timeout = null)
    {
        var controlTimeout = timeout ?? DefaultControlTimeout;
        using var controller = new ServiceController(serviceName);
        controller.Refresh();

        if (action == WindowsServiceControlAction.Start)
        {
            StartController(controller, serviceName, controlTimeout);
            return;
        }

        StopController(controller, serviceName, controlTimeout);
    }

    public static void Install(
        string exePath,
        string serviceName = TunProxyProduct.ServiceName,
        string displayName = TunProxyProduct.DisplayName,
        string description = TunProxyProduct.ServiceDescription)
    {
        ConfigureAutomaticStart(exePath, serviceName, displayName);
        RunSc($"description {serviceName} \"{description}\"");
        RunSc($"sdset {serviceName} D:(A;;CCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;LCRPWPCR;;;IU)");
        RunSc($"failure {serviceName} reset= 60 actions= restart/1000");
        RunSc($"failureflag {serviceName} 1");
    }

    public static (int ExitCode, string Output) ConfigureAutomaticStart(
        string exePath,
        string serviceName = TunProxyProduct.ServiceName,
        string displayName = TunProxyProduct.DisplayName)
    {
        return RunSc($"create {serviceName} binPath= \"{exePath}\" start= auto DisplayName= \"{displayName}\"");
    }

    public static (int ExitCode, string Output) EnsureAutomaticStart(
        string serviceName = TunProxyProduct.ServiceName)
    {
        return RunSc($"config {serviceName} start= auto");
    }

    public static void Uninstall(string serviceName = TunProxyProduct.ServiceName)
    {
        RunSc($"stop {serviceName}");
        Thread.Sleep(2000);
        RunSc($"delete {serviceName}");
    }

    public static (int ExitCode, string Output) RunSc(string args)
    {
        var process = Process.Start(new ProcessStartInfo
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

    public static bool IsStartEnabled(ServiceControllerStatus status) =>
        status == ServiceControllerStatus.Stopped;

    public static bool IsStopEnabled(ServiceControllerStatus status) =>
        status is ServiceControllerStatus.Running
            or ServiceControllerStatus.StartPending
            or ServiceControllerStatus.Paused
            or ServiceControllerStatus.PausePending
            or ServiceControllerStatus.ContinuePending;

    internal static bool IsAccessDenied(Exception ex) =>
        ex is Win32Exception { NativeErrorCode: 5 } ||
        ex is InvalidOperationException { InnerException: Win32Exception { NativeErrorCode: 5 } };

    internal static string GetScVerb(WindowsServiceControlAction action) =>
        action == WindowsServiceControlAction.Start ? "start" : "stop";

    private static void StartController(
        ServiceController controller,
        string serviceName,
        TimeSpan timeout)
    {
        if (controller.Status == ServiceControllerStatus.Running)
        {
            return;
        }

        if (controller.Status == ServiceControllerStatus.StartPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
            return;
        }

        if (controller.Status == ServiceControllerStatus.StopPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            controller.Refresh();
        }

        if (controller.Status != ServiceControllerStatus.Stopped)
        {
            throw new InvalidOperationException($"Cannot start {serviceName} while it is {controller.Status}.");
        }

        controller.Start();
        controller.WaitForStatus(ServiceControllerStatus.Running, timeout);
    }

    private static void StopController(
        ServiceController controller,
        string serviceName,
        TimeSpan timeout)
    {
        if (controller.Status == ServiceControllerStatus.Stopped)
        {
            return;
        }

        if (controller.Status == ServiceControllerStatus.StopPending)
        {
            controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
            return;
        }

        if (!controller.CanStop)
        {
            throw new InvalidOperationException($"Cannot stop {serviceName} while it is {controller.Status}.");
        }

        controller.Stop();
        controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
    }

    private static void RunElevatedServiceControl(
        WindowsServiceControlAction action,
        string serviceName,
        TimeSpan timeout)
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"{GetScVerb(action)} {serviceName}",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        process?.WaitForExit((int)timeout.TotalMilliseconds);
    }
}

public enum WindowsServiceControlAction
{
    Start,
    Stop
}
