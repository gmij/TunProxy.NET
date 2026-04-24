using System.Text.Json;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class AppConfigStore
{
    private readonly string _configPath;

    public AppConfigStore(string? configPath = null)
    {
        _configPath = configPath ?? AppPaths.ConfigFilePath;
    }

    public string ConfigPath => _configPath;

    public AppConfig LoadOrCreate(Action<AppConfig>? configureNewConfig = null)
    {
        if (TryLoad(out var loaded))
        {
            Log.Information("Configuration loaded from {Path}", _configPath);
            return loaded;
        }

        Log.Information("Configuration file not found. Creating sample file at {Path}", _configPath);
        var config = new AppConfig();
        configureNewConfig?.Invoke(config);
        Save(config);
        return config;
    }

    public AppConfig LoadOrClone(AppConfig fallback)
    {
        if (TryLoad(out var loaded))
        {
            return loaded;
        }

        var clone = new AppConfig();
        clone.ApplyFrom(fallback);
        return clone;
    }

    public void Save(AppConfig config)
    {
        EnsureDirectory();
        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));
    }

    public async Task SaveAsync(AppConfig config, CancellationToken ct)
    {
        EnsureDirectory();
        await File.WriteAllTextAsync(
            _configPath,
            JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig),
            ct);
    }

    private bool TryLoad(out AppConfig config)
    {
        config = new AppConfig();
        if (!File.Exists(_configPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            config = JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig) ?? new AppConfig();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to read configuration file. Using defaults.");
            return false;
        }
    }

    private void EnsureDirectory()
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }
}
