using TunProxy.CLI;
using Serilog;

namespace TunProxy.Tests;

public class SerilogDefaultsTests
{
    [Fact]
    public void SerilogDefaultsResource_IsEmbeddedInCliAssembly()
    {
        var resources = typeof(ApiEndpoints).Assembly.GetManifestResourceNames();

        Assert.Contains("serilog.defaults.json", resources);
    }

    [Fact]
    public void EmbeddedSerilogConfiguration_LoadsDefaultPolicy()
    {
        var configuration = EmbeddedSerilogConfiguration.Build(assembly: typeof(ApiEndpoints).Assembly);

        Assert.Equal("Information", configuration["Serilog:MinimumLevel:Default"]);
        Assert.Equal("Warning", configuration["Serilog:MinimumLevel:Override:Microsoft"]);
        Assert.Equal("3", configuration["Serilog:WriteTo:1:Args:retainedFileCountLimit"]);
        Assert.EndsWith("logs/tunproxy-.log", NormalizePath(EmbeddedSerilogConfiguration.GetFileSinkPath(configuration)));
    }

    [Fact]
    public void EmbeddedSerilogConfiguration_AppliesRuntimeOverrides()
    {
        var configuration = EmbeddedSerilogConfiguration.Build(
            logFilePathOverride: "custom.log",
            minimumLevelOverride: "Debug",
            assembly: typeof(ApiEndpoints).Assembly);

        Assert.Equal("Debug", configuration["Serilog:MinimumLevel:Default"]);
        Assert.EndsWith("custom.log", NormalizePath(EmbeddedSerilogConfiguration.GetFileSinkPath(configuration)));
    }

    [Fact]
    public void EmbeddedSerilogConfiguration_PrefersLocalFileOverEmbeddedResource()
    {
        var baseDirectory = Path.Combine(Path.GetTempPath(), $"serilog-local-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDirectory);
        try
        {
            File.WriteAllText(Path.Combine(baseDirectory, "serilog.defaults.json"), """
                {
                  "Serilog": {
                    "MinimumLevel": {
                      "Default": "Error"
                    },
                    "WriteTo": [
                      {
                        "Name": "File",
                        "Args": {
                          "path": "local.log"
                        }
                      }
                    ]
                  }
                }
                """);

            var configuration = EmbeddedSerilogConfiguration.Build(
                baseDirectory: baseDirectory,
                assembly: typeof(ApiEndpoints).Assembly);

            Assert.Equal("Error", configuration["Serilog:MinimumLevel:Default"]);
            Assert.EndsWith("local.log", NormalizePath(EmbeddedSerilogConfiguration.GetFileSinkPath(configuration)));
        }
        finally
        {
            Directory.Delete(baseDirectory, recursive: true);
        }
    }

    [Fact]
    public void EmbeddedSerilogConfiguration_ReturnsEmptyConfigWhenNoFileOrResourceExists()
    {
        var assembly = typeof(SerilogDefaultsTests).Assembly;
        var configuration = EmbeddedSerilogConfiguration.Build(
            baseDirectory: Path.Combine(Path.GetTempPath(), $"serilog-missing-{Guid.NewGuid():N}"),
            assembly: assembly);

        Assert.Null(configuration["Serilog:MinimumLevel:Default"]);
        Assert.Null(EmbeddedSerilogConfiguration.GetFileSinkPath(configuration));
    }

    [Fact]
    public async Task CreateLogger_WiresFileSinkFromConfiguration()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"serilog-runtime-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var logFilePath = Path.Combine(tempDirectory, "runtime.log");

        try
        {
            var configuration = EmbeddedSerilogConfiguration.Build(
                logFilePathOverride: logFilePath,
                assembly: typeof(ApiEndpoints).Assembly);
            var logger = Assert.IsAssignableFrom<ILogger>(Program.CreateLogger(configuration));
            logger.Information("serilog-file-sink-check");
            (logger as IDisposable)?.Dispose();

            Assert.True(File.Exists(logFilePath));
            Assert.Contains("serilog-file-sink-check", await File.ReadAllTextAsync(logFilePath));
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static string? NormalizePath(string? path) =>
        path?.Replace('\\', '/');
}
