using System.Text.Json.Serialization;

namespace TunProxy.Tray;

internal sealed class ServiceStatusDto
{
    public string Mode { get; set; } = "proxy";
    public bool IsRunning { get; set; }
    public bool IsDownloading { get; set; }
    public int ActiveConnections { get; set; }
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyType { get; set; } = "";
}

internal sealed class AppConfigDto
{
    public LocalProxyConfigDto LocalProxy { get; set; } = new();
}

internal sealed class LocalProxyConfigDto
{
    public int ListenPort { get; set; } = 8080;
    public bool SetSystemProxy { get; set; } = true;
    public string BypassList { get; set; } = "<local>;localhost;127.0.0.1;10.*;192.168.*";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ServiceStatusDto))]
[JsonSerializable(typeof(AppConfigDto))]
internal partial class TrayJsonContext : JsonSerializerContext
{
}
