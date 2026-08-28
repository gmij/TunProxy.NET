using System.Net;

namespace TunProxy.Core.Route;

/// <summary>
/// 路由服务抽象接口（Windows netsh / Linux ip route / macOS route）
/// </summary>
public interface IRouteService
{
    /// <summary>添加绕过 TUN 的直连路由（走原始网关）</summary>
    bool AddBypassRoute(string ip, int prefixLength = 32);

    /// <summary>为上游代理添加绕过路由；允许使用目标所在的 VPN/Overlay 网卡。</summary>
    bool AddProxyBypassRoute(string ip, int prefixLength = 32) => AddBypassRoute(ip, prefixLength);

    /// <summary>删除绕过路由</summary>
    bool RemoveBypassRoute(string ip);

    /// <summary>删除由当前服务实例添加并跟踪的绕过路由</summary>
    bool RemoveTrackedBypassRoute(string ip) => RemoveBypassRoute(ip);

    /// <summary>添加默认路由（全局流量走 TUN）</summary>
    bool AddDefaultRoute();

    /// <summary>删除默认路由（恢复原始网络）</summary>
    bool RemoveDefaultRoute();

    /// <summary>获取原始默认网关（非 TUN 网关）</summary>
    string? GetOriginalDefaultGateway();

    /// <summary>获取当前安全 DIRECT 出口的本地 IPv4 地址。</summary>
    IPAddress? GetDirectOutboundAddress() => null;

    /// <summary>重新读取平台路由状态；用于应用内重启和网络变化后的自愈。</summary>
    void RefreshRouteState()
    {
    }

    IPAddress? GetLocalAddressForDestination(IPAddress destination) => null;

    /// <summary>删除所有通过 AddBypassRoute 添加的绕过路由，恢复干净的路由表</summary>
    void ClearAllBypassRoutes();
}
