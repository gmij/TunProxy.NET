namespace TunProxy.Core;

public static class TunProxyProduct
{
    public const string ServiceName = "TunProxyService";
    public const string DisplayName = "TunProxy Service";
    public const string ServiceDescription = "TUN-mode transparent proxy service for Windows";
    public const string RestartRequestFileName = "tunproxy.restart";
    public const string ConfigFileName = "tunproxy.json";
    public const string WintunDllFileName = "wintun.dll";
    public const int ApiPort = 50000;
    public const string ApiBaseUrl = "http://localhost:50000";
}
