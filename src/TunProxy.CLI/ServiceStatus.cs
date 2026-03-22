using TunProxy.Core.Metrics;

namespace TunProxy.CLI;

/// <summary>
/// 服务状态信息（用于 Web API）
/// </summary>
public class ServiceStatus
{
    public bool IsRunning { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyType { get; set; } = "";
    public int ActiveConnections { get; set; }
    public MetricsSnapshot Metrics { get; set; } = new();
}
