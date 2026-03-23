using System.Text.Json.Serialization;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

/// <summary>
/// JSON 序列化上下文（AOT 兼容）
/// </summary>
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(ProxyConfig))]
[JsonSerializable(typeof(TunConfig))]
[JsonSerializable(typeof(RouteConfig))]
[JsonSerializable(typeof(LoggingConfig))]
public partial class AppJsonContext : JsonSerializerContext
{
}
