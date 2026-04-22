using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TunProxy.Core.Configuration;
using TunProxy.Core.Packets;

namespace TunProxy.CLI;

public sealed class RouteDecisionService
{
    private readonly AppConfig _config;
    private readonly Func<string, bool> _isInGfwList;
    private readonly Func<IPAddress, string?> _getCountryCode;
    private readonly Func<bool> _isGeoReady;
    private readonly Func<string, CancellationToken, Task<IPAddress?>> _resolveHost;
    private readonly ConcurrentDictionary<string, HostResolutionCacheEntry> _hostResolutionCache = new(StringComparer.OrdinalIgnoreCase);

    public RouteDecisionService(AppConfig config, GeoIpService? geoIpService, GfwListService? gfwListService)
        : this(
            config,
            domain => gfwListService?.IsInGfwList(domain) == true,
            ip => geoIpService?.GetCountryCode(ip),
            () => geoIpService?.IsInitialized == true,
            ResolveHostAsync)
    {
    }

    internal RouteDecisionService(
        AppConfig config,
        Func<string, bool>? isInGfwList = null,
        Func<IPAddress, string?>? getCountryCode = null,
        Func<bool>? isGeoReady = null,
        Func<string, CancellationToken, Task<IPAddress?>>? resolveHost = null)
    {
        _config = config;
        _isInGfwList = isInGfwList ?? (_ => false);
        _getCountryCode = getCountryCode ?? (_ => null);
        _isGeoReady = isGeoReady ?? (() => getCountryCode != null);
        _resolveHost = resolveHost ?? ResolveHostAsync;
    }

    public Task<RouteDecision> DecideForDomainAsync(string host, CancellationToken ct) =>
        DecideAsync(host, destinationIp: null, resolveHost: true, ct);

    public Task<RouteDecision> DecideForTunAsync(string? host, IPAddress destinationIp, CancellationToken ct) =>
        DecideAsync(host, destinationIp, resolveHost: true, ct);

    public Task<RouteDecision> DecideForObservedAddressAsync(string? host, IPAddress destinationIp, CancellationToken ct) =>
        DecideAsync(host, destinationIp, resolveHost: false, ct);

    private async Task<RouteDecision> DecideAsync(
        string? host,
        IPAddress? destinationIp,
        bool resolveHost,
        CancellationToken ct)
    {
        var domain = NormalizeDomain(host);
        if (domain != null)
        {
            if (_config.Route.EnableGfwList && _isInGfwList(domain))
            {
                return RouteDecision.Proxy("GFW", domain, destinationIp);
            }
        }

        if (destinationIp != null && ProtocolInspector.IsPrivateIp(destinationIp))
        {
            return RouteDecision.Direct("PrivateIP", domain, destinationIp);
        }

        IPAddress? resolvedIp = null;
        if (resolveHost && domain != null)
        {
            resolvedIp = await ResolveHostWithCacheAsync(domain, ct);
            if (resolvedIp != null && ProtocolInspector.IsPrivateIp(resolvedIp))
            {
                return RouteDecision.Direct("ResolvedPrivateIP", domain, resolvedIp);
            }
        }

        if (IsGlobalRouteMode())
        {
            return RouteDecision.Proxy("Global", domain, destinationIp ?? resolvedIp);
        }

        var geoIp = destinationIp ?? resolvedIp;
        if (_config.Route.EnableGeo && geoIp != null && _isGeoReady())
        {
            var country = _getCountryCode(geoIp);
            if (country == null &&
                destinationIp != null &&
                resolvedIp != null &&
                !resolvedIp.Equals(destinationIp))
            {
                var resolvedCountry = _getCountryCode(resolvedIp);
                if (resolvedCountry != null)
                {
                    var resolvedShouldProxy = ShouldProxyByGeo(resolvedCountry, _config.Route.GeoProxy, _config.Route.GeoDirect);
                    return resolvedShouldProxy
                        ? RouteDecision.Proxy($"Geo:{resolvedCountry}", domain, resolvedIp)
                        : RouteDecision.Direct($"Geo:{resolvedCountry}", domain, resolvedIp);
                }
            }

            var shouldProxy = ShouldProxyByGeo(country, _config.Route.GeoProxy, _config.Route.GeoDirect);
            return shouldProxy
                ? RouteDecision.Proxy(country == null ? "GeoUnknown" : $"Geo:{country}", domain, geoIp)
                : RouteDecision.Direct(country == null ? "GeoUnknown" : $"Geo:{country}", domain, geoIp);
        }

        return RouteDecision.Proxy("Default", domain, destinationIp);
    }

    private async Task<IPAddress?> ResolveHostWithCacheAsync(string domain, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_hostResolutionCache.TryGetValue(domain, out var cached) &&
            cached.ExpiresUtc > now)
        {
            return cached.Address;
        }

        var address = await _resolveHost(domain, ct);
        _hostResolutionCache[domain] = new HostResolutionCacheEntry(
            address,
            now.AddMinutes(address == null ? 1 : 5));
        return address;
    }

    internal static bool ShouldProxyByGeo(
        string? country,
        IReadOnlyCollection<string> geoProxy,
        IReadOnlyCollection<string> geoDirect)
    {
        if (string.IsNullOrWhiteSpace(country))
        {
            return true;
        }

        if (ContainsCountry(geoDirect, country))
        {
            return false;
        }

        if (ContainsCountry(geoProxy, country))
        {
            return true;
        }

        if (geoDirect.Count > 0)
        {
            return true;
        }

        if (geoProxy.Count > 0)
        {
            return false;
        }

        return !country.Equals("CN", StringComparison.OrdinalIgnoreCase);
    }

    internal static string? NormalizeDomain(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var value = host.Trim().TrimEnd('.').Trim('[', ']');
        if (IPAddress.TryParse(value, out _))
        {
            return null;
        }

        var colon = value.LastIndexOf(':');
        if (colon > 0 && int.TryParse(value[(colon + 1)..], out _))
        {
            value = value[..colon];
        }

        return value.Length == 0 ? null : value.ToLowerInvariant();
    }

    private static bool ContainsCountry(IEnumerable<string> countries, string country) =>
        countries.Any(item => item.Equals(country, StringComparison.OrdinalIgnoreCase));

    private bool IsGlobalRouteMode() =>
        string.Equals(_config.Route.Mode, "global", StringComparison.OrdinalIgnoreCase);

    private static async Task<IPAddress?> ResolveHostAsync(string domain, CancellationToken ct)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct);
            return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork);
        }
        catch
        {
            return null;
        }
    }
}

internal sealed record HostResolutionCacheEntry(IPAddress? Address, DateTime ExpiresUtc);

public sealed record RouteDecision(
    bool ShouldProxy,
    string Reason,
    string? Domain,
    IPAddress? EvaluatedIp)
{
    public static RouteDecision Proxy(string reason, string? domain, IPAddress? evaluatedIp) =>
        new(true, reason, domain, evaluatedIp);

    public static RouteDecision Direct(string reason, string? domain, IPAddress? evaluatedIp) =>
        new(false, reason, domain, evaluatedIp);
}
