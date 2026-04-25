using System.ComponentModel;
using System.Runtime.Versioning;
using System.ServiceProcess;
using TunProxy.Core.WindowsServices;

namespace TunProxy.Tests;

[SupportedOSPlatform("windows")]
public class WindowsServiceManagerTests
{
    [Fact]
    public void IsStartEnabled_OnlyAllowsStoppedServices()
    {
        Assert.True(WindowsServiceManager.IsStartEnabled(ServiceControllerStatus.Stopped));
        Assert.False(WindowsServiceManager.IsStartEnabled(ServiceControllerStatus.Running));
        Assert.False(WindowsServiceManager.IsStartEnabled(ServiceControllerStatus.StartPending));
        Assert.False(WindowsServiceManager.IsStartEnabled(ServiceControllerStatus.StopPending));
    }

    [Fact]
    public void IsStopEnabled_AllowsRunningAndPendingRunningStates()
    {
        Assert.True(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.Running));
        Assert.True(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.StartPending));
        Assert.True(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.Paused));
        Assert.True(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.PausePending));
        Assert.True(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.ContinuePending));
        Assert.False(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.Stopped));
        Assert.False(WindowsServiceManager.IsStopEnabled(ServiceControllerStatus.StopPending));
    }

    [Fact]
    public void IsAccessDenied_RecognizesWin32AccessDeniedErrors()
    {
        Assert.True(WindowsServiceManager.IsAccessDenied(new Win32Exception(5)));
        Assert.True(WindowsServiceManager.IsAccessDenied(
            new InvalidOperationException("access denied", new Win32Exception(5))));
        Assert.False(WindowsServiceManager.IsAccessDenied(new Win32Exception(2)));
        Assert.False(WindowsServiceManager.IsAccessDenied(new InvalidOperationException("other")));
    }

    [Fact]
    public void GetScVerb_MapsServiceActions()
    {
        Assert.Equal("start", WindowsServiceManager.GetScVerb(WindowsServiceControlAction.Start));
        Assert.Equal("stop", WindowsServiceManager.GetScVerb(WindowsServiceControlAction.Stop));
    }
}
