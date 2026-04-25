using TunProxy.Core;

namespace TunProxy.CLI;

internal static class AppPaths
{
    public static string BaseDirectory => AppContext.BaseDirectory;

    public static string ConfigFilePath => Path.Combine(BaseDirectory, TunProxyProduct.ConfigFileName);

    public static string RuntimeStateFilePath => Path.Combine(BaseDirectory, TunProxyProduct.RuntimeStateFileName);

    public static string RestartRequestPath => Path.Combine(BaseDirectory, TunProxyProduct.RestartRequestFileName);

    public static string WintunDllPath => Path.Combine(BaseDirectory, TunProxyProduct.WintunDllFileName);

    public static string DefaultLogFilePath => Path.Combine(BaseDirectory, "logs", "tunproxy-.log");
}
