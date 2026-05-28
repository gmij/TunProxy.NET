using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal sealed class CliRuleResourceCommandHandler
{
    private readonly AppConfigStore _configStore;
    private readonly TextWriter _output;
    private readonly Action<AppConfig> _configureNewConfig;

    public CliRuleResourceCommandHandler(
        AppConfigStore? configStore = null,
        TextWriter? output = null,
        Action<AppConfig>? configureNewConfig = null)
    {
        _configStore = configStore ?? new AppConfigStore();
        _output = output ?? Console.Out;
        _configureNewConfig = configureNewConfig ?? (_ => { });
    }

    public async Task<int?> TryHandleAsync(string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || !IsResourceCommand(args[0]))
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

                case "status":
                    await PrintStatusAsync(ct);
                    return 0;

                case "prepare":
                case "download":
                    await PrepareAsync(remainingArgs, ct);
                    return 0;

                default:
                    await _output.WriteLineAsync($"Unknown resource command: {command}");
                    await _output.WriteLineAsync(BuildHelpText());
                    return 1;
            }
        }
        catch (Exception ex)
        {
            await _output.WriteLineAsync($"Resource command failed: {ex.Message}");
            return 1;
        }
    }

    private async Task PrintStatusAsync(CancellationToken ct)
    {
        var config = _configStore.LoadOrCreate(_configureNewConfig);
        var service = new RuleResourceService();
        var status = await service.GetStatusAsync(config);

        await _output.WriteLineAsync("Rule resource status");
        await _output.WriteLineAsync($"Config file: {_configStore.ConfigPath}");
        await _output.WriteLineAsync(FormatStatusLine(status.Geo));
        await _output.WriteLineAsync(FormatStatusLine(status.Gfw));
        ct.ThrowIfCancellationRequested();
    }

    private async Task PrepareAsync(string[] args, CancellationToken ct)
    {
        var config = _configStore.LoadOrCreate(_configureNewConfig);
        var target = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "enabled";
        var results = new List<string>();
        var ranAny = false;

        if (target is "enabled" or "geo" or "all")
        {
            if (target != "enabled" || config.Route.EnableGeo)
            {
                ranAny = true;
                using var geo = new GeoIpService(config.Route.GeoIpDbPath);
                var ok = await geo.InitializeAsync(ct, config.Proxy, downloadIfMissing: true);
                results.Add($"geo: {(ok ? "ready" : "failed")} ({geo.DatabasePath})");
            }
        }

        if (target is "enabled" or "gfw" or "all")
        {
            if (target != "enabled" || config.Route.EnableGfwList)
            {
                ranAny = true;
                var gfw = new GfwListService(config.Route.GfwListUrl, config.Route.GfwListPath);
                var ok = await gfw.InitializeAsync(ct, config.Proxy, downloadIfMissing: true);
                results.Add($"gfw: {(ok ? "ready" : "failed")} ({gfw.ListPath})");
            }
        }

        if (!ranAny)
        {
            await _output.WriteLineAsync("No enabled rule resources need preparation. Use `resource prepare all` to force both.");
            return;
        }

        foreach (var result in results)
        {
            await _output.WriteLineAsync(result);
        }
    }

    private static bool IsResourceCommand(string value) =>
        string.Equals(value, "resource", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "resources", StringComparison.OrdinalIgnoreCase);

    private static string FormatStatusLine(RuleResourceStatus status) =>
        $"{status.Name}: enabled={status.Enabled}, exists={status.Exists}, valid={status.Ready}, path={status.Path}";

    internal static string BuildHelpText()
    {
        return string.Join(
            Environment.NewLine,
            [
                "TunProxy command-line rule resource management",
                "  TunProxy.CLI resource status",
                "  TunProxy.CLI resource prepare",
                "  TunProxy.CLI resource prepare geo",
                "  TunProxy.CLI resource prepare gfw",
                "  TunProxy.CLI resource prepare all",
                "",
                "`prepare` without a target only prepares resources that are enabled in tunproxy.json."
            ]);
    }
}
