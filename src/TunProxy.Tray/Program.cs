using System.Runtime.InteropServices;
using TunProxy.Core.Localization;
using TunProxy.Tray;

try
{
    var app = new TrayApp();
    app.Run();
}
catch (Exception ex)
{
    NativeMethods.MessageBoxW(
        IntPtr.Zero,
        ex.ToString(),
        LocalizedText.GetCurrent("Tray.StartupFailed"),
        0x10);
}
