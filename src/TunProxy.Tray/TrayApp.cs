using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using TunProxy.Core.Localization;
using static TunProxy.Tray.NativeMethods;

namespace TunProxy.Tray;

internal enum ServiceState
{
    Unknown,
    Stopped,
    Running,
    Error,
    Downloading
}

internal sealed class TrayApp : IDisposable
{
    private const string ClassName = "TunProxyTrayWnd";
    private const string SvcName = "TunProxyService";
    private const string ApiBase = "http://localhost:50000";
    private const uint TimerId = 1;

    private const int CMD_STATUS = 1;
    private const int CMD_START = 2;
    private const int CMD_STOP = 3;
    private const int CMD_INSTALL = 4;
    private const int CMD_UNINSTALL = 5;
    private const int CMD_CONSOLE = 6;
    private const int CMD_EXIT = 7;
    private const int CMD_FIX_TUN = 8;

    private IntPtr _hWnd;
    private NOTIFYICONDATAW _nid;
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(2) };
    private WndProc? _wndProcDelegate;

    private IntPtr _iconGray;
    private IntPtr _iconGreen;
    private IntPtr _iconRed;
    private IntPtr _iconYellow;

    private ServiceState _currentState = ServiceState.Unknown;
    private bool _startEnabled = true;
    private bool _stopEnabled;
    private bool _installEnabled = true;
    private bool _uninstallEnabled;
    private string _statusText = LocalizedText.GetCurrent("Tray.Status.Unknown");
    private bool _disposed;
    private bool _autoStarted;
    private bool _serviceModeMismatch;
    private readonly SystemProxy _sysProxy = new();

    private static string AppDir =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public void Run()
    {
        var hInstance = GetModuleHandleW(IntPtr.Zero);
        _wndProcDelegate = WndProcHandler;

        var windowClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            lpszClassName = ClassName
        };
        RegisterClassExW(ref windowClass);

        _hWnd = CreateWindowExW(
            0,
            ClassName,
            "TunProxy Tray",
            0,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        _iconGray = CreateCircleIcon(160, 160, 160, 60, 60, 60);
        _iconGreen = CreateCircleIcon(50, 205, 50, 0, 100, 0);
        _iconRed = CreateCircleIcon(220, 50, 50, 139, 0, 0);
        _iconYellow = CreateCircleIcon(255, 200, 0, 160, 110, 0);

        _nid = new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _iconGray,
            szTip = Format("Tray.Tooltip", LocalizedText.GetCurrent("Tray.Status.Unknown"))
        };
        Shell_NotifyIconW(NIM_ADD, ref _nid);

        UpdateInstallState();
        SetTimer(_hWnd, TimerId, 3000, IntPtr.Zero);
        _ = Task.Run(PollStatusAsync);

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Dispose();
    }

    private static string Text(string key) => LocalizedText.GetCurrent(key);

    private static string Format(string key, params object[] args) =>
        LocalizedText.FormatCurrent(key, args);

    private IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                var mouseMsg = (uint)(lParam.ToInt64() & 0xFFFF);
                if (mouseMsg == WM_RBUTTONUP)
                {
                    ShowContextMenu();
                }
                else if (mouseMsg == WM_LBUTTONDBLCLK)
                {
                    OpenConsole();
                }
                return IntPtr.Zero;

            case WM_COMMAND:
                HandleCommand(wParam.ToInt32());
                return IntPtr.Zero;

            case WM_TIMER:
                if ((nuint)wParam == TimerId)
                {
                    _ = Task.Run(PollStatusAsync);
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                Shell_NotifyIconW(NIM_DELETE, ref _nid);
                KillTimer(_hWnd, TimerId);
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hWnd, msg, wParam, lParam);
        }
    }

    private void ShowContextMenu()
    {
        UpdateInstallState();

        var menu = CreatePopupMenu();

        AppendMenuW(menu, MF_STRING | MF_GRAYED, CMD_STATUS, _statusText);
        AppendMenuW(menu, MF_SEPARATOR, 0, null);

        AppendMenuW(menu, MF_STRING | (_startEnabled ? MF_ENABLED : MF_GRAYED), CMD_START, Text("Tray.Menu.StartService"));
        AppendMenuW(menu, MF_STRING | (_stopEnabled ? MF_ENABLED : MF_GRAYED), CMD_STOP, Text("Tray.Menu.StopService"));
        AppendMenuW(menu, MF_SEPARATOR, 0, null);

        AppendMenuW(
            menu,
            MF_STRING | (_installEnabled ? MF_ENABLED : MF_GRAYED),
            CMD_INSTALL,
            _installEnabled ? Text("Tray.Install.Menu") : Text("Tray.Install.MenuInstalled"));

        AppendMenuW(
            menu,
            MF_STRING | (_uninstallEnabled ? MF_ENABLED : MF_GRAYED),
            CMD_UNINSTALL,
            Text("Tray.Uninstall.Menu"));

        if (_serviceModeMismatch)
        {
            AppendMenuW(menu, MF_SEPARATOR, 0, null);
            AppendMenuW(menu, MF_STRING, CMD_FIX_TUN, Text("Tray.FixTun.Menu"));
        }

        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, CMD_CONSOLE, Text("Tray.Menu.OpenConsole"));
        AppendMenuW(menu, MF_STRING, CMD_EXIT, Text("Tray.Menu.Exit"));

        SetForegroundWindow(_hWnd);
        GetCursorPos(out var point);
        var selected = TrackPopupMenu(
            menu,
            TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RETURNCMD,
            point.x,
            point.y,
            0,
            _hWnd,
            IntPtr.Zero);

        DestroyMenu(menu);

        if (selected > 0)
        {
            HandleCommand(selected);
        }
    }

    private void HandleCommand(int commandId)
    {
        switch (commandId)
        {
            case CMD_START:
                StartService();
                break;
            case CMD_STOP:
                StopService();
                break;
            case CMD_INSTALL:
                InstallService();
                break;
            case CMD_UNINSTALL:
                UninstallService();
                break;
            case CMD_CONSOLE:
                OpenConsole();
                break;
            case CMD_FIX_TUN:
                _ = FixTunModeAsync();
                break;
            case CMD_EXIT:
                DestroyWindow(_hWnd);
                break;
        }
    }

    private async Task PollStatusAsync()
    {
        UpdateInstallState();

        ServiceState newState;
        string statusText;
        ServiceStatusDto? lastDto = null;

        try
        {
            var dto = await _httpClient.GetFromJsonAsync(
                $"{ApiBase}/api/status",
                TrayJsonContext.Default.ServiceStatusDto);

            lastDto = dto;
            if (dto == null)
            {
                newState = ServiceState.Error;
                statusText = Text("Tray.Status.ResponseParseFailed");
            }
            else if (dto.IsDownloading)
            {
                newState = ServiceState.Downloading;
                statusText = Text("Tray.Status.Downloading");
            }
            else if (dto.IsRunning)
            {
                newState = ServiceState.Running;
                var modeLabel = dto.Mode == "tun" ? Text("Tray.Mode.Tun") : Text("Tray.Mode.Proxy");
                statusText = Format("Tray.Status.RunningSummary", modeLabel, dto.ActiveConnections);
            }
            else
            {
                newState = ServiceState.Error;
                statusText = Text("Tray.Status.Error");
            }
        }
        catch (HttpRequestException)
        {
            newState = ServiceState.Stopped;
            statusText = IsServiceInstalled()
                ? Text("Tray.Status.ServiceInstalledStopped")
                : Text("Tray.Status.ServiceNotStarted");
        }
        catch (TaskCanceledException)
        {
            newState = ServiceState.Stopped;
            statusText = IsServiceInstalled()
                ? Text("Tray.Status.ServiceNoResponse")
                : Text("Tray.Status.ServiceNotStarted");
        }

        if (!_autoStarted)
        {
            _autoStarted = true;
            if (newState is ServiceState.Stopped or ServiceState.Unknown)
            {
                StartService();
            }
        }

        if (newState == ServiceState.Running)
        {
            if (lastDto?.Mode != "tun")
            {
                TrySetSystemProxy();
            }
            else
            {
                _sysProxy.DisableForTun();
            }
        }
        else if (newState != ServiceState.Running && _currentState == ServiceState.Running)
        {
            if (_sysProxy.IsApplied)
            {
                _sysProxy.Restore();
            }
        }

        if (newState != _currentState || statusText != _statusText)
        {
            ApplyState(newState, statusText);
        }

        _serviceModeMismatch =
            IsServiceInstalled()
            && newState == ServiceState.Running
            && lastDto?.Mode == "proxy";
    }

    private void ApplyState(ServiceState state, string statusText)
    {
        _currentState = state;
        _statusText = statusText;

        _nid.hIcon = state switch
        {
            ServiceState.Running => _iconGreen,
            ServiceState.Error => _iconRed,
            ServiceState.Downloading => _iconYellow,
            _ => _iconGray
        };

        var tooltip = Format("Tray.Tooltip", statusText);
        _nid.szTip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        _nid.uFlags = NIF_ICON | NIF_TIP;
        Shell_NotifyIconW(NIM_MODIFY, ref _nid);

        _startEnabled = state is ServiceState.Stopped or ServiceState.Unknown;
        _stopEnabled = state is ServiceState.Running or ServiceState.Downloading or ServiceState.Error;
    }

    private static bool IsServiceInstalled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\{SvcName}");
            return key != null;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateInstallState()
    {
        var installed = IsServiceInstalled();
        _installEnabled = !installed;
        _uninstallEnabled = installed;
    }

    private async Task FixTunModeAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync($"{ApiBase}/api/enable-tun", null);
            if (response.IsSuccessStatusCode)
            {
                MessageBoxW(
                    _hWnd,
                    Text("Tray.FixTun.SuccessMessage"),
                    Text("Tray.Caption.RepairSuccess"),
                    MB_OK | MB_ICONINFO);
                _serviceModeMismatch = false;
            }
            else
            {
                MessageBoxW(
                    _hWnd,
                    Format("Tray.FixTun.ApiError", (int)response.StatusCode),
                    Text("Tray.Caption.RepairFailed"),
                    MB_OK | MB_ICONERROR);
            }
        }
        catch (Exception ex)
        {
            MessageBoxW(
                _hWnd,
                Format("Tray.FixTun.CannotConnectApi", ex.Message),
                Text("Tray.Caption.RepairFailed"),
                MB_OK | MB_ICONERROR);
        }
    }

    private async void TrySetSystemProxy()
    {
        try
        {
            var dto = await _httpClient.GetFromJsonAsync(
                $"{ApiBase}/api/config",
                TrayJsonContext.Default.AppConfigDto);

            if (dto == null || !dto.LocalProxy.SetSystemProxy)
            {
                return;
            }

            _sysProxy.Set(
                $"127.0.0.1:{dto.LocalProxy.ListenPort}",
                dto.LocalProxy.BypassList);
        }
        catch
        {
        }
    }

    private void StartService()
    {
        try
        {
            if (IsServiceInstalled())
            {
                using var controller = new ServiceController(SvcName);
                try
                {
                    controller.Start();
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }

            var cliPath = Path.Combine(AppDir, "TunProxy.CLI.exe");
            if (!File.Exists(cliPath))
            {
                MessageBoxW(
                    _hWnd,
                    Format("Tray.FileMissing", cliPath),
                    Text("Tray.Caption.Error"),
                    MB_OK | MB_ICONERROR);
                return;
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = true,
                WorkingDirectory = AppDir
            };

            if (IsServiceInstalled())
            {
                processStartInfo.Verb = "runas";
            }

            Process.Start(processStartInfo);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 1223)
            {
                return;
            }

            MessageBoxW(
                _hWnd,
                Format("Tray.Start.Failed", ex.Message),
                Text("Tray.Caption.Error"),
                MB_OK | MB_ICONERROR);
        }
    }

    private void StopService()
    {
        try
        {
            if (IsServiceInstalled())
            {
                using var controller = new ServiceController(SvcName);
                try
                {
                    controller.Stop();
                }
                catch (InvalidOperationException)
                {
                }

                return;
            }

            var processes = Process.GetProcessesByName("TunProxy.CLI");
            if (processes.Length == 0)
            {
                MessageBoxW(
                    _hWnd,
                    Text("Tray.NoRunningCli"),
                    Text("Tray.Caption.Info"),
                    MB_OK | MB_ICONINFO);
                return;
            }

            foreach (var process in processes)
            {
                process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBoxW(
                _hWnd,
                Format("Tray.Stop.Failed", ex.Message),
                Text("Tray.Caption.Error"),
                MB_OK | MB_ICONERROR);
        }
    }

    private void InstallService()
    {
        try
        {
            var cliPath = Path.Combine(AppDir, "TunProxy.CLI.exe");
            if (!File.Exists(cliPath))
            {
                MessageBoxW(
                    _hWnd,
                    Format("Tray.FileMissing", cliPath),
                    Text("Tray.Caption.Error"),
                    MB_OK | MB_ICONERROR);
                return;
            }

            var process = Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "--install",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppDir
            });
            process?.WaitForExit(15000);

            UpdateInstallState();

            if (!IsServiceInstalled())
            {
                return;
            }

            foreach (var runningProcess in Process.GetProcessesByName("TunProxy.CLI"))
            {
                try
                {
                    runningProcess.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                runningProcess.Dispose();
            }

            try
            {
                using var controller = new ServiceController(SvcName);
                controller.Start();
            }
            catch (InvalidOperationException)
            {
            }

            MessageBoxW(
                _hWnd,
                Text("Tray.Install.SuccessMessage"),
                Text("Tray.Caption.InstallComplete"),
                MB_OK | MB_ICONINFO);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (ex is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 1223)
            {
                return;
            }

            MessageBoxW(
                _hWnd,
                Format("Tray.Install.Failed", ex.Message),
                Text("Tray.Caption.Error"),
                MB_OK | MB_ICONERROR);
        }
    }

    private void UninstallService()
    {
        var result = MessageBoxW(
            _hWnd,
            Text("Tray.Uninstall.ConfirmMessage"),
            Text("Tray.Caption.ConfirmUninstall"),
            MB_YESNO | MB_ICONQUESTION);

        if (result != IDYES)
        {
            return;
        }

        try
        {
            var cliPath = Path.Combine(AppDir, "TunProxy.CLI.exe");
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = "--uninstall",
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppDir
            });
            process?.WaitForExit(15000);

            WaitForServiceUninstalled(TimeSpan.FromSeconds(10));
            UpdateInstallState();
            ApplyState(_currentState, _statusText);
            StartService();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBoxW(
                _hWnd,
                Format("Tray.Uninstall.Failed", ex.Message),
                Text("Tray.Caption.Error"),
                MB_OK | MB_ICONERROR);
        }
    }

    private static void WaitForServiceUninstalled(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsServiceInstalled())
            {
                return;
            }

            Thread.Sleep(300);
        }
    }

    private void OpenConsole()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ApiBase,
                UseShellExecute = true
            });
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            MessageBoxW(
                _hWnd,
                Format("Tray.OpenBrowserFailed", ex.Message),
                Text("Tray.Caption.Error"),
                MB_OK | MB_ICONERROR);
        }
    }

    private static unsafe IntPtr CreateCircleIcon(
        byte fillR,
        byte fillG,
        byte fillB,
        byte borderR,
        byte borderG,
        byte borderB)
    {
        const int size = 16;

        var bitmapHeader = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = size,
            biHeight = -size,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };

        var deviceContext = CreateCompatibleDC(IntPtr.Zero);
        var bitmap = CreateDIBSection(deviceContext, ref bitmapHeader, 0, out var bits, IntPtr.Zero, 0);
        var oldBitmap = SelectObject(deviceContext, bitmap);

        var pixels = (uint*)bits;
        var centerX = size / 2f;
        var centerY = size / 2f;
        var outerRadius = size / 2f - 1f;
        var innerRadius = outerRadius - 1.5f;

        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var deltaX = x + 0.5f - centerX;
                var deltaY = y + 0.5f - centerY;
                var distance = MathF.Sqrt(deltaX * deltaX + deltaY * deltaY);

                if (distance <= innerRadius)
                {
                    pixels[y * size + x] = PackArgb(255, fillR, fillG, fillB);
                }
                else if (distance <= outerRadius)
                {
                    pixels[y * size + x] = PackArgb(255, borderR, borderG, borderB);
                }
                else if (distance <= outerRadius + 1f)
                {
                    var alpha = 1f - (distance - outerRadius);
                    pixels[y * size + x] = PackArgb((byte)(alpha * 255), borderR, borderG, borderB);
                }
                else
                {
                    pixels[y * size + x] = 0;
                }
            }
        }

        SelectObject(deviceContext, oldBitmap);
        DeleteDC(deviceContext);

        var mask = CreateBitmap(size, size, 1, 1, IntPtr.Zero);
        var iconInfo = new ICONINFO
        {
            fIcon = true,
            hbmMask = mask,
            hbmColor = bitmap
        };

        var icon = CreateIconIndirect(ref iconInfo);

        DeleteObject(bitmap);
        DeleteObject(mask);

        return icon;
    }

    private static uint PackArgb(byte alpha, byte red, byte green, byte blue) =>
        (uint)((alpha << 24) | (red << 16) | (green << 8) | blue);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();

        if (_sysProxy.IsApplied)
        {
            _sysProxy.Restore();
        }

        if (_iconGray != IntPtr.Zero) DestroyIcon(_iconGray);
        if (_iconGreen != IntPtr.Zero) DestroyIcon(_iconGreen);
        if (_iconRed != IntPtr.Zero) DestroyIcon(_iconRed);
        if (_iconYellow != IntPtr.Zero) DestroyIcon(_iconYellow);
    }
}
