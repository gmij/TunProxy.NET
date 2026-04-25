using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Serilog;
using TunProxy.Core;
using TunProxy.Core.Configuration;
using TunProxy.Core.WindowsServices;

namespace TunProxy.CLI;

internal sealed class RestartCoordinator
{
    private readonly string _restartRequestPath;
    private readonly Func<string?> _getProcessPath;
    private readonly Func<string, string, bool> _startDetachedProcess;
    private readonly Action _stopApplication;

    public RestartCoordinator(
        IHostApplicationLifetime? lifetime = null,
        string? restartRequestPath = null,
        Func<string?>? getProcessPath = null,
        Func<string, string, bool>? startDetachedProcess = null)
    {
        _restartRequestPath = restartRequestPath ?? AppPaths.RestartRequestPath;
        _getProcessPath = getProcessPath ?? (() => Environment.ProcessPath);
        _startDetachedProcess = startDetachedProcess ?? TryStartDetachedProcess;
        _stopApplication = lifetime == null ? () => { } : lifetime.StopApplication;
    }

    public RestartRequestResult RequestRestart(AppConfig config)
    {
        RequestTrayRestart();
        var helperStarted = TryStartRestartHelper(config);
        Log.Information("[RESTART] Web restart requested. HelperStarted={HelperStarted}", helperStarted);

        if (helperStarted)
        {
            _ = StopApplicationSoonAsync();
        }
        else
        {
            Log.Warning("[RESTART] Restart helper was not started; keeping the current process alive.");
        }

        return new RestartRequestResult(helperStarted);
    }

    public void RequestStop()
    {
        _ = StopApplicationSoonAsync();
    }

    internal bool TryStartRestartHelper(AppConfig config)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!WindowsServiceManager.IsInstalled() && config.Tun.Enabled)
                {
                    return TryStartElevatedServiceInstallHelper();
                }

                var command = BuildWindowsRestartCommand(
                    WindowsServiceManager.IsInstalled(),
                    _getProcessPath());
                return !string.IsNullOrWhiteSpace(command) &&
                       _startDetachedProcess("cmd.exe", "/c " + command);
            }

            var exePath = _getProcessPath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            return _startDetachedProcess("/bin/sh", $"-c \"sleep 2; \\\"{exePath}\\\"\"");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start restart helper.");
            return false;
        }
    }

    internal static string BuildWindowsRestartCommand(bool serviceInstalled, string? processPath) =>
        serviceInstalled
            ? $"timeout /t 2 /nobreak > nul & sc stop {TunProxyProduct.ServiceName} & timeout /t 2 /nobreak > nul & sc start {TunProxyProduct.ServiceName}"
            : string.IsNullOrWhiteSpace(processPath)
                ? string.Empty
                : $"timeout /t 2 /nobreak > nul & start \"\" \"{processPath}\"";

    private void RequestTrayRestart()
    {
        try
        {
            File.WriteAllText(_restartRequestPath, DateTimeOffset.UtcNow.ToString("O"));
            Log.Information("[RESTART] Restart marker written for tray: {Path}", _restartRequestPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to write restart marker: {Path}", _restartRequestPath);
        }
    }

    private bool TryStartElevatedServiceInstallHelper()
    {
        try
        {
            var exePath = _getProcessPath();
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--install",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
            Log.Information("[RESTART] Started elevated service installer for TUN mode.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start elevated service installer.");
            return false;
        }
    }

    private static bool TryStartDetachedProcess(string fileName, string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[RESTART] Failed to start helper process: {FileName} {Arguments}", fileName, arguments);
            return false;
        }
    }

    private async Task StopApplicationSoonAsync()
    {
        await Task.Delay(300);
        _stopApplication();
    }
}

internal sealed record RestartRequestResult(bool HelperStarted);
