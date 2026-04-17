namespace TunProxy.CLI;

internal static class AppPathResolver
{
    internal static string ResolveAppFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return AppContext.BaseDirectory;
        }

        return Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);
    }
}
