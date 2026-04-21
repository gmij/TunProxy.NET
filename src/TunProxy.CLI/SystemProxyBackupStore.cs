using System.Text.Json;
using Serilog;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class SystemProxyBackupStore
{
    private readonly AppConfig _config;
    private readonly string _configPath;
    private readonly object _sync = new();

    public SystemProxyBackupStore(AppConfig config, string? configPath = null)
    {
        _config = config;
        _configPath = configPath ?? Path.Combine(AppContext.BaseDirectory, "tunproxy.json");
    }

    public SystemProxyBackupConfig? GetBackup()
    {
        lock (_sync)
        {
            var latest = LoadLatestConfig();
            var backup = latest.LocalProxy.SystemProxyBackup;
            if (!backup.Captured)
            {
                _config.LocalProxy.SystemProxyBackup.Clear();
                return null;
            }

            _config.LocalProxy.SystemProxyBackup.ApplyFrom(backup);
            return backup.Clone();
        }
    }

    public bool HasBackup() => GetBackup() != null;

    public void SaveIfMissing(SystemProxyBackupConfig backup)
    {
        if (!backup.Captured)
        {
            return;
        }

        lock (_sync)
        {
            var latest = LoadLatestConfig();
            if (latest.LocalProxy.SystemProxyBackup.Captured)
            {
                _config.LocalProxy.SystemProxyBackup.ApplyFrom(latest.LocalProxy.SystemProxyBackup);
                return;
            }

            latest.LocalProxy.SystemProxyBackup.ApplyFrom(backup);
            _config.LocalProxy.SystemProxyBackup.ApplyFrom(backup);
            Save(latest);
            Log.Information("[SYSTEM-PROXY] Original system proxy settings captured in configuration.");
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            var latest = LoadLatestConfig();
            latest.LocalProxy.SystemProxyBackup.Clear();
            _config.LocalProxy.SystemProxyBackup.Clear();
            Save(latest);
            Log.Information("[SYSTEM-PROXY] Original system proxy backup cleared from configuration.");
        }
    }

    private AppConfig LoadLatestConfig()
    {
        if (!File.Exists(_configPath))
        {
            return CloneCurrentConfig();
        }

        try
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize(json, AppJsonContext.Default.AppConfig)
                   ?? CloneCurrentConfig();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[SYSTEM-PROXY] Failed to read configuration while handling system proxy backup.");
            return CloneCurrentConfig();
        }
    }

    private void Save(AppConfig config)
    {
        var directory = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_configPath, JsonSerializer.Serialize(config, AppJsonContext.Default.AppConfig));
    }

    private AppConfig CloneCurrentConfig()
    {
        var clone = new AppConfig();
        clone.ApplyFrom(_config);
        return clone;
    }
}
