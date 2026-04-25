using System.ServiceProcess;

namespace TunProxy.Tray;

internal static class TrayRestartRequestPolicy
{
    public static bool ShouldConsumeRestartRequest(
        bool restartRequestExists,
        bool serviceInstalled,
        ServiceControllerStatus? serviceStatus)
    {
        if (!restartRequestExists)
        {
            return false;
        }

        if (!serviceInstalled)
        {
            return true;
        }

        return serviceStatus == ServiceControllerStatus.Stopped;
    }
}
