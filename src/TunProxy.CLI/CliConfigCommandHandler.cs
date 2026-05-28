using System.Text;
using System.Text.Json;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class CliConfigCommandHandler
{
    private readonly AppConfigStore _configStore;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly Action<AppConfig> _configureNewConfig;
    private readonly Func<ProxyConfig, CancellationToken, Task<UpstreamProxyStatus>> _checkProxyAsync;
    private readonly Func<AppConfig, CancellationToken, Task<IReadOnlyList<string>>> _prepareEnabledResourcesAsync;

    public CliConfigCommandHandler(
        AppConfigStore? configStore = null,
        TextReader? input = null,
        TextWriter? output = null,
        Action<AppConfig>? configureNewConfig = null,
        Func<ProxyConfig, CancellationToken, Task<UpstreamProxyStatus>>? checkProxyAsync = null,
        Func<AppConfig, CancellationToken, Task<IReadOnlyList<string>>>? prepareEnabledResourcesAsync = null)
    {
        _configStore = configStore ?? new AppConfigStore();
        _input = input ?? Console.In;
        _output = output ?? Console.Out;
        _configureNewConfig = configureNewConfig ?? (_ => { });
        _checkProxyAsync = checkProxyAsync ?? UpstreamProxyHealthChecker.CheckAsync;
        _prepareEnabledResourcesAsync = prepareEnabledResourcesAsync ?? PrepareEnabledResourcesAsync;
    }

    public async Task<int?> TryHandleAsync(string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || !string.Equals(args[0], "config", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var command = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : "help";
        var remainingArgs = args.Skip(2).ToArray();

        try
        {
            switch (command)
            {
                case "help":
                case "--help":
                case "-h":
                    await _output.WriteLineAsync(BuildHelpText());
                    return 0;

                case "path":
                    await _output.WriteLineAsync(_configStore.ConfigPath);
                    return 0;

                case "show":
                    await ShowConfigAsync(ct);
                    return 0;

                case "init":
                    await InitConfigAsync(remainingArgs, ct);
                    return 0;

                case "set":
                    await SetConfigAsync(remainingArgs, ct);
                    return 0;

                case "wizard":
                    await RunWizardAsync(ct);
                    return 0;

                default:
                    await _output.WriteLineAsync($"Unknown config command: {command}");
                    await _output.WriteLineAsync(BuildHelpText());
                    return 1;
            }
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync($"Configuration command failed: {ex.Message}");
            return 1;
        }
    }

    private async Task ShowConfigAsync(CancellationToken ct)
    {
        var config = _configStore.LoadOrCreate(_configureNewConfig);
        var json = JsonSerializer.SerializeToUtf8Bytes(config, AppJsonContext.Default.AppConfig);
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            document.WriteTo(writer);
        }

        await _output.WriteLineAsync(Encoding.UTF8.GetString(stream.ToArray()));
        ct.ThrowIfCancellationRequested();
    }

    private async Task InitConfigAsync(string[] args, CancellationToken ct)
    {
        if (File.Exists(_configStore.ConfigPath))
        {
            await _output.WriteLineAsync($"Configuration already exists: {_configStore.ConfigPath}");
            await _output.WriteLineAsync("Use `config set` or `config show`.");
            return;
        }

        var config = new AppConfig();
        _configureNewConfig(config);
        CommandLineConfigOverrides.Apply(config, args, strict: true);
        await _configStore.SaveAsync(config, ct);
        await PrintSavedSummaryAsync(config);
    }

    private async Task SetConfigAsync(string[] args, CancellationToken ct)
    {
        var config = _configStore.LoadOrCreate(_configureNewConfig);
        CommandLineConfigOverrides.Apply(config, args, strict: true);
        await _configStore.SaveAsync(config, ct);
        await PrintSavedSummaryAsync(config);
    }

    private async Task RunWizardAsync(CancellationToken ct)
    {
        var config = _configStore.LoadOrCreate(_configureNewConfig);

        await _output.WriteLineAsync("TunProxy command-line setup");
        await _output.WriteLineAsync($"Config file: {_configStore.ConfigPath}");
        await _output.WriteLineAsync("Default flow: HTTP upstream proxy + TUN mode + enabled GFW/Geo resource preparation.");
        await _output.WriteLineAsync(string.Empty);

        config.Proxy.Host = await PromptStringAsync("Upstream proxy host", config.Proxy.Host);
        config.Proxy.Port = await PromptIntAsync("Upstream proxy port", config.Proxy.Port);
        config.Proxy.Type = await PromptChoiceAsync("Upstream proxy type", "Http", ["Http", "Socks5"]);
        config.Proxy.Username = await PromptOptionalStringAsync("Upstream proxy username", config.Proxy.Username);
        config.Proxy.Password = await PromptOptionalSecretAsync("Upstream proxy password", config.Proxy.Password);

        config.LocalProxy.ListenPort = await PromptIntAsync("Local proxy listen port", config.LocalProxy.ListenPort);
        config.Tun.DnsServer = await PromptStringAsync("TUN DNS server", config.Tun.DnsServer);
        config.Route.EnableGfwList = await PromptBoolAsync("Enable GFWList rules", true);
        config.Route.EnableGeo = await PromptBoolAsync("Enable GeoIP rules", true);
        config.Tun.FakeIpMode = await PromptBoolAsync("Enable fake IP mode", config.Tun.FakeIpMode);
        CommandLineConfigOverrides.Apply(config, ["--mode", "tun"], strict: true);

        await _configStore.SaveAsync(config, ct);
        await PrintSavedSummaryAsync(config);
        await RunWizardChecksAndPreparationAsync(config, ct);
    }

    private async Task PrintSavedSummaryAsync(AppConfig config)
    {
        await _output.WriteLineAsync($"Saved configuration to {_configStore.ConfigPath}");
        await _output.WriteLineAsync(
            $"Proxy: {config.Proxy.Type} {config.Proxy.Host}:{config.Proxy.Port} | Mode: {(config.Tun.Enabled ? "TUN" : "Local Proxy")}");
        await _output.WriteLineAsync("Start normally with `TunProxy.CLI` after configuration is complete.");
    }

    private async Task RunWizardChecksAndPreparationAsync(AppConfig config, CancellationToken ct)
    {
        await _output.WriteLineAsync(string.Empty);
        await _output.WriteLineAsync("Checking upstream proxy...");

        var proxyStatus = await _checkProxyAsync(config.Proxy, ct);
        foreach (var target in proxyStatus.Targets)
        {
            var summary = target.IsOk
                ? $"{target.Name}: OK ({target.StatusCode}, {target.ElapsedMs} ms)"
                : $"{target.Name}: FAILED ({target.StatusCode?.ToString() ?? target.Error ?? "unknown error"})";
            await _output.WriteLineAsync(summary);
        }

        await _output.WriteLineAsync(proxyStatus.IsAvailable
            ? "Upstream proxy check passed."
            : "Upstream proxy check did not fully pass. Resource downloads and TUN routing may still fail.");

        await _output.WriteLineAsync(string.Empty);
        await _output.WriteLineAsync("Preparing enabled rule resources...");
        var resourceResults = await _prepareEnabledResourcesAsync(config, ct);
        foreach (var line in resourceResults)
        {
            await _output.WriteLineAsync(line);
        }
    }

    private static async Task<IReadOnlyList<string>> PrepareEnabledResourcesAsync(AppConfig config, CancellationToken ct)
    {
        var results = new List<string>();

        if (config.Route.EnableGeo)
        {
            using var geo = new GeoIpService(config.Route.GeoIpDbPath);
            var ok = await geo.InitializeAsync(ct, config.Proxy, downloadIfMissing: true);
            results.Add($"geo: {(ok ? "ready" : "failed")} ({geo.DatabasePath})");
        }
        else
        {
            results.Add("geo: disabled");
        }

        if (config.Route.EnableGfwList)
        {
            var gfw = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
            var ok = await gfw.InitializeAsync(ct, config.Proxy, downloadIfMissing: true);
            results.Add($"gfw: {(ok ? "ready" : "failed")} ({gfw.ListPath})");
        }
        else
        {
            results.Add("gfw: disabled");
        }

        return results;
    }

    private async Task<string> PromptStringAsync(string label, string currentValue)
    {
        await _output.WriteAsync($"{label} [{currentValue}]: ");
        var value = await _input.ReadLineAsync();
        return string.IsNullOrWhiteSpace(value) ? currentValue : value.Trim();
    }

    private async Task<string?> PromptOptionalStringAsync(string label, string? currentValue)
    {
        var displayValue = string.IsNullOrEmpty(currentValue) ? "empty" : currentValue;
        await _output.WriteAsync($"{label} [{displayValue}] (enter '-' to clear): ");
        var value = await _input.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(value))
        {
            return currentValue;
        }

        return value.Trim() == "-" ? null : value.Trim();
    }

    private async Task<string?> PromptOptionalSecretAsync(string label, string? currentValue)
    {
        var displayValue = string.IsNullOrEmpty(currentValue) ? "empty" : "configured";
        await _output.WriteAsync($"{label} [{displayValue}] (enter '-' to clear): ");
        var value = await _input.ReadLineAsync();
        if (string.IsNullOrWhiteSpace(value))
        {
            return currentValue;
        }

        return value.Trim() == "-" ? null : value.Trim();
    }

    private async Task<int> PromptIntAsync(string label, int currentValue)
    {
        while (true)
        {
            await _output.WriteAsync($"{label} [{currentValue}]: ");
            var value = await _input.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(value))
            {
                return currentValue;
            }

            if (int.TryParse(value.Trim(), out var parsed))
            {
                return parsed;
            }

            await _output.WriteLineAsync("Please enter a valid integer.");
        }
    }

    private async Task<bool> PromptBoolAsync(string label, bool currentValue)
    {
        while (true)
        {
            var current = currentValue ? "Y/n" : "y/N";
            await _output.WriteAsync($"{label} [{current}]: ");
            var value = await _input.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(value))
            {
                return currentValue;
            }

            var normalized = value.Trim().ToLowerInvariant();
            if (normalized is "y" or "yes" or "true" or "1")
            {
                return true;
            }

            if (normalized is "n" or "no" or "false" or "0")
            {
                return false;
            }

            await _output.WriteLineAsync("Please answer yes or no.");
        }
    }

    private async Task<string> PromptChoiceAsync(string label, string currentValue, IReadOnlyList<string> choices)
    {
        while (true)
        {
            await _output.WriteAsync($"{label} [{currentValue}] ({string.Join("/", choices)}): ");
            var value = await _input.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(value))
            {
                return currentValue;
            }

            var normalized = value.Trim();
            var match = choices.FirstOrDefault(choice => string.Equals(choice, normalized, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match))
            {
                return match;
            }

            await _output.WriteLineAsync($"Supported values: {string.Join(", ", choices)}");
        }
    }

    internal static string BuildHelpText()
    {
        var builder = new StringBuilder();
        builder.AppendLine("TunProxy command-line configuration");
        builder.AppendLine("  TunProxy.CLI config path");
        builder.AppendLine("  TunProxy.CLI config show");
        builder.AppendLine("  TunProxy.CLI config init [options]");
        builder.AppendLine("  TunProxy.CLI config set [options]");
        builder.AppendLine("  TunProxy.CLI config wizard  # defaults to HTTP + TUN and prepares resources");
        builder.AppendLine();
        builder.AppendLine("Common options:");
        builder.AppendLine("  --proxy, -p host:port");
        builder.AppendLine("  --type, -t socks5|http");
        builder.AppendLine("  --username, -u USER");
        builder.AppendLine("  --password, -w PASS");
        builder.AppendLine("  --mode proxy|tun");
        builder.AppendLine("  --listen-port PORT");
        builder.AppendLine("  --dns-server IP");
        builder.AppendLine("  --enable-gfw | --disable-gfw");
        builder.AppendLine("  --enable-geo | --disable-geo");
        builder.AppendLine("  --fake-ip | --no-fake-ip");
        builder.AppendLine();
        builder.AppendLine("Example:");
        builder.AppendLine("  TunProxy.CLI config set --proxy 127.0.0.1:7890 --type socks5 --mode tun");
        return builder.ToString().TrimEnd();
    }
}
