using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

/// <summary>
/// 代理服务统一接口（本地代理模式和 TUN 模式共用）
/// </summary>
public interface IProxyService
{
    ServiceStatus GetStatus();
    Task StartAsync(CancellationToken ct);
    Task StopAsync();
    IReadOnlyList<string> GetDirectIps();
    Task RefreshRuleResourcesAsync(CancellationToken ct);
}
