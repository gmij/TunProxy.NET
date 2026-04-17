namespace TunProxy.Core.Route;

/// <summary>
/// 路由服务抽象接口（Windows netsh / Linux ip route / macOS route）
/// </summary>
public interface IRouteService
{
    /// <summary>添加绕过 TUN 的直连路由（走原始网关）</summary>
    bool AddBypassRoute(string ip, int prefixLength = 32);

    /// <summary>删除绕过路由</summary>
    bool RemoveBypassRoute(string ip);

    /// <summary>添加默认路由（全局流量走 TUN）</summary>
    bool AddDefaultRoute();

    /// <summary>删除默认路由（恢复原始网络）</summary>
    bool RemoveDefaultRoute();

    /// <summary>获取原始默认网关（非 TUN 网关）</summary>
    string? GetOriginalDefaultGateway();

    /// <summary>删除所有通过 AddBypassRoute 添加的绕过路由，恢复干净的路由表</summary>
    void ClearAllBypassRoutes();
}
