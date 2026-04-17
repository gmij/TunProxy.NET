using System.Text.Json.Serialization;
using TunProxy.Core.Configuration;
using TunProxy.Core.Metrics;

namespace TunProxy.CLI;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ProxyConfig))]
[JsonSerializable(typeof(TunConfig))]
[JsonSerializable(typeof(LocalProxyConfig))]
[JsonSerializable(typeof(RouteConfig))]
[JsonSerializable(typeof(LoggingConfig))]
[JsonSerializable(typeof(ServiceStatus))]
[JsonSerializable(typeof(MetricsSnapshot))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(LogEntry))]
[JsonSerializable(typeof(LogEntry[]))]
public partial class AppJsonContext : JsonSerializerContext
{
}
