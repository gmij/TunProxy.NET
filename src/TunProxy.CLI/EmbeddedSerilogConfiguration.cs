using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace TunProxy.CLI;

internal static class EmbeddedSerilogConfiguration
{
    private const string WriteToSectionKey = "Serilog:WriteTo";
    private const string ResourceName = "serilog.defaults.json";
    public const string FileSinkPathKey = "Serilog:WriteTo:1:Args:path";
    private const string MinimumLevelDefaultKey = "Serilog:MinimumLevel:Default";

    public static IConfigurationRoot Build(
        string? logFilePathOverride = null,
        string? minimumLevelOverride = null,
        string? baseDirectory = null,
        Assembly? assembly = null)
    {
        var baseConfiguration = BuildConfiguration(overrides: null, baseDirectory, assembly);
        var defaultLogPath = GetFileSinkPath(baseConfiguration);
        var logFilePath = string.IsNullOrWhiteSpace(logFilePathOverride)
            ? defaultLogPath
            : logFilePathOverride;

        Dictionary<string, string?>? overrides = null;

        if (!string.IsNullOrWhiteSpace(logFilePath))
        {
            var fileSinkPathKey = FindFileSinkPathKey(baseConfiguration) ?? FileSinkPathKey;
            overrides = new Dictionary<string, string?>
            {
                [fileSinkPathKey] = AppPathResolver.ResolveAppFilePath(logFilePath)
            };
        }

        if (!string.IsNullOrWhiteSpace(minimumLevelOverride))
        {
            overrides ??= [];
            overrides[MinimumLevelDefaultKey] = minimumLevelOverride;
        }

        return BuildConfiguration(overrides, baseDirectory, assembly);
    }

    private static IConfigurationRoot BuildConfiguration(
        IEnumerable<KeyValuePair<string, string?>>? overrides,
        string? baseDirectory,
        Assembly? assembly)
    {
        var builder = new ConfigurationBuilder();
        using var stream = OpenConfigurationStream(baseDirectory, assembly ?? typeof(EmbeddedSerilogConfiguration).Assembly);
        if (stream != null)
        {
            builder.AddJsonStream(stream);
        }

        if (overrides != null)
        {
            builder.AddInMemoryCollection(overrides);
        }

        return builder.Build();
    }

    public static string? GetFileSinkPath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var fileSinkPathKey = FindFileSinkPathKey(configuration);
        return fileSinkPathKey is null ? null : configuration[fileSinkPathKey];
    }

    private static string? FindFileSinkPathKey(IConfiguration configuration)
    {
        foreach (var sinkSection in configuration.GetSection(WriteToSectionKey).GetChildren())
        {
            if (!string.Equals(sinkSection["Name"], "File", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var pathKey = $"{sinkSection.Path}:Args:path";
            if (!string.IsNullOrWhiteSpace(configuration[pathKey]))
            {
                return pathKey;
            }
        }

        return string.IsNullOrWhiteSpace(configuration[FileSinkPathKey])
            ? null
            : FileSinkPathKey;
    }

    private static Stream? OpenConfigurationStream(string? baseDirectory, Assembly assembly)
    {
        var localPath = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, ResourceName);
        if (File.Exists(localPath))
        {
            return File.OpenRead(localPath);
        }

        return OpenEmbeddedResourceStream(assembly);
    }

    private static Stream? OpenEmbeddedResourceStream(Assembly assembly)
    {
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(static name => name.Equals(ResourceName, StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                return stream;
            }
        }

        return null;
    }
}
