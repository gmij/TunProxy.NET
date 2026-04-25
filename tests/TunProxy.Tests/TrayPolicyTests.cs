using System.ServiceProcess;
using TunProxy.Tray;

namespace TunProxy.Tests;

public class TrayPolicyTests
{
    [Fact]
    public void RestartRequestPolicy_WaitsForInstalledServiceToStop()
    {
        Assert.False(TrayRestartRequestPolicy.ShouldConsumeRestartRequest(
            restartRequestExists: true,
            serviceInstalled: true,
            serviceStatus: ServiceControllerStatus.Running));

        Assert.True(TrayRestartRequestPolicy.ShouldConsumeRestartRequest(
            restartRequestExists: true,
            serviceInstalled: true,
            serviceStatus: ServiceControllerStatus.Stopped));
    }

    [Fact]
    public void RestartRequestPolicy_AllowsStandaloneProcessRestart()
    {
        Assert.True(TrayRestartRequestPolicy.ShouldConsumeRestartRequest(
            restartRequestExists: true,
            serviceInstalled: false,
            serviceStatus: null));
    }

    [Fact]
    public void SystemProxyPolicy_DisablesProxyInTunMode()
    {
        var action = TraySystemProxyPolicy.Resolve(
            ServiceState.Running,
            ServiceState.Running,
            runtimeMode: "tun",
            config: new AppConfigDto(),
            systemProxyApplied: true,
            apiBase: "http://localhost:50000");

        Assert.Equal(TraySystemProxyActionKind.DisableForTun, action.Kind);
    }

    [Fact]
    public void SystemProxyPolicy_SelectsPacForLocalProxyMode()
    {
        var config = new AppConfigDto();
        config.LocalProxy.SystemProxyMode = "pac";

        var action = TraySystemProxyPolicy.Resolve(
            ServiceState.Running,
            ServiceState.Stopped,
            runtimeMode: "proxy",
            config,
            systemProxyApplied: false,
            apiBase: "http://localhost:50000");

        Assert.Equal(TraySystemProxyActionKind.SetPac, action.Kind);
        Assert.Equal("http://localhost:50000/proxy.pac", action.PacUrl);
    }

    [Fact]
    public void SystemProxyPolicy_RestoresWhenLeavingRunningState()
    {
        var action = TraySystemProxyPolicy.Resolve(
            ServiceState.Stopped,
            ServiceState.Running,
            runtimeMode: null,
            config: null,
            systemProxyApplied: true,
            apiBase: "http://localhost:50000");

        Assert.Equal(TraySystemProxyActionKind.Restore, action.Kind);
    }
}
