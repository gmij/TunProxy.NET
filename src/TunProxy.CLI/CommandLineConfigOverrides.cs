using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal static class CommandLineConfigOverrides
{
    public static void Apply(AppConfig config, IReadOnlyList<string> args, bool strict)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(args);

        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            switch (token)
            {
                case "--proxy" or "-p":
                {
                    var value = ReadRequiredValue(args, ref i, token);
                    ApplyProxyEndpoint(config, value);
                    break;
                }

                case "--type" or "-t":
                    config.Proxy.Type = ReadRequiredValue(args, ref i, token);
                    break;

                case "--username" or "-u":
                    config.Proxy.Username = ReadRequiredValue(args, ref i, token);
                    break;

                case "--password" or "-w":
                    config.Proxy.Password = ReadRequiredValue(args, ref i, token);
                    break;

                case "--mode":
                    ApplyMode(config, ReadRequiredValue(args, ref i, token));
                    break;

                case "--listen-host":
                    config.LocalProxy.ListenHost = ReadRequiredValue(args, ref i, token);
                    break;

                case "--listen-port":
                    config.LocalProxy.ListenPort = ParseRequiredInt(ReadRequiredValue(args, ref i, token), token);
                    break;

                case "--enable-lan-proxy":
                    config.LocalProxy.EnableLanProxy = true;
                    break;

                case "--no-lan-proxy":
                    config.LocalProxy.EnableLanProxy = false;
                    break;

                case "--system-proxy-mode":
                    ApplySystemProxyMode(config, ReadRequiredValue(args, ref i, token));
                    break;

                case "--dns-server":
                    config.Tun.DnsServer = ReadRequiredValue(args, ref i, token);
                    break;

                case "--tun-address":
                    config.Tun.IpAddress = ReadRequiredValue(args, ref i, token);
                    break;

                case "--tun-subnet-mask":
                    config.Tun.SubnetMask = ReadRequiredValue(args, ref i, token);
                    break;

                case "--route-mode":
                    config.Route.Mode = ReadRequiredValue(args, ref i, token);
                    break;

                case "--enable-geo":
                    config.Route.EnableGeo = true;
                    break;

                case "--disable-geo":
                    config.Route.EnableGeo = false;
                    break;

                case "--enable-gfw":
                    config.Route.EnableGfwList = true;
                    break;

                case "--disable-gfw":
                    config.Route.EnableGfwList = false;
                    break;

                case "--fake-ip":
                    config.Tun.FakeIpMode = true;
                    break;

                case "--no-fake-ip":
                    config.Tun.FakeIpMode = false;
                    break;

                case "--gfwlist-url":
                    config.Route.GfwListUrl = ReadRequiredValue(args, ref i, token);
                    break;

                case "--gfwlist-path":
                    config.Route.GfwListPath = ReadRequiredValue(args, ref i, token);
                    break;

                case "--geoip-db":
                    config.Route.GeoIpDbPath = ReadRequiredValue(args, ref i, token);
                    break;

                case "--log-level":
                    config.Logging.MinimumLevel = ReadRequiredValue(args, ref i, token);
                    break;

                default:
                    if (strict && IsOption(token))
                    {
                        throw new InvalidOperationException($"Unknown configuration option: {token}");
                    }

                    break;
            }
        }
    }

    internal static void ApplyProxyEndpoint(AppConfig config, string value)
    {
        var (host, port) = ParseProxyEndpoint(value);
        config.Proxy.Host = host;
        if (port.HasValue)
        {
            config.Proxy.Port = port.Value;
        }
    }

    internal static (string Host, int? Port) ParseProxyEndpoint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException("--proxy requires a non-empty value.");
        }

        var trimmed = value.Trim();

        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var end = trimmed.IndexOf(']');
            if (end <= 0)
            {
                throw new InvalidOperationException($"Invalid proxy endpoint: {value}");
            }

            var host = trimmed[1..end];
            if (end == trimmed.Length - 1)
            {
                return (host, null);
            }

            if (trimmed[end + 1] != ':')
            {
                throw new InvalidOperationException($"Invalid proxy endpoint: {value}");
            }

            return (host, ParseRequiredInt(trimmed[(end + 2)..], "--proxy"));
        }

        var firstColon = trimmed.IndexOf(':');
        var lastColon = trimmed.LastIndexOf(':');
        if (firstColon > 0 && firstColon == lastColon)
        {
            return (trimmed[..firstColon], ParseRequiredInt(trimmed[(firstColon + 1)..], "--proxy"));
        }

        return (trimmed, null);
    }

    private static void ApplyMode(AppConfig config, string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        switch (normalized)
        {
            case "proxy":
            case "local":
                config.Tun.Enabled = false;
                if (config.LocalProxy.SystemProxyMode == SystemProxyModes.Tun)
                {
                    config.LocalProxy.SystemProxyMode = SystemProxyModes.None;
                }

                break;

            case "tun":
                config.Tun.Enabled = true;
                config.LocalProxy.SystemProxyMode = SystemProxyModes.Tun;
                break;

            default:
                throw new InvalidOperationException($"Unsupported mode '{value}'. Use 'proxy' or 'tun'.");
        }
    }

    private static void ApplySystemProxyMode(AppConfig config, string value)
    {
        config.LocalProxy.SystemProxyMode = SystemProxyModes.Normalize(value);
        if (config.LocalProxy.SystemProxyMode == SystemProxyModes.Tun)
        {
            config.Tun.Enabled = true;
        }
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new InvalidOperationException($"{option} requires a value.");
        }

        index++;
        return args[index].Trim();
    }

    private static int ParseRequiredInt(string value, string option)
    {
        if (!int.TryParse(value, out var result))
        {
            throw new InvalidOperationException($"{option} requires an integer value.");
        }

        return result;
    }

    private static bool IsOption(string value) =>
        value.StartsWith("-", StringComparison.Ordinal);
}
