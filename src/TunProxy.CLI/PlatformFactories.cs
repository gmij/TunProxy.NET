using TunProxy.Core.Configuration;
using TunProxy.Core.Route;
using TunProxy.Core.Tun;

namespace TunProxy.CLI;

/// <summary>
/// TUN 设备工厂（根据当前平台创建对应实现）
/// </summary>
public static class TunDeviceFactory
{
    public static ITunDevice Create(TunConfig config)
    {
        if (OperatingSystem.IsWindows()) return new WintunDevice(config);
        if (OperatingSystem.IsLinux())   return new LinuxTunDevice();
        if (OperatingSystem.IsMacOS())   return new MacOsTunDevice();
        throw new PlatformNotSupportedException(
            $"不支持的平台：{System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }
}

/// <summary>
/// 路由服务工厂（根据当前平台创建对应实现）
/// </summary>
public static class RouteServiceFactory
{
    public static IRouteService Create(TunConfig config)
    {
        if (OperatingSystem.IsWindows()) return new WindowsRouteService(config.IpAddress, config.SubnetMask);
        if (OperatingSystem.IsLinux())   return new LinuxRouteService(config.IpAddress, config.SubnetMask);
        if (OperatingSystem.IsMacOS())   return new MacOsRouteService(config.IpAddress, config.SubnetMask);
        throw new PlatformNotSupportedException();
    }
}
