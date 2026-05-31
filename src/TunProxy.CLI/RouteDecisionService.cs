using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using TunProxy.Core.Configuration;
using TunProxy.Core.Packets;

namespace TunProxy.CLI;

public sealed class RouteDecisionService
{
    private static readonly TimeSpan StickyDecisionTtl = TimeSpan.FromSeconds(45);

    private readonly AppConfig _config;
    private readonly HashSet<string> _probeDirectDomainSuffixes;
    private readonly HashSet<string> _proxyDomainSuffixes;
    private readonly HashSet<string> _directDomainSuffixes;
    private readonly Func<string, bool> _isInGfwList;
    private readonly Func<IPAddress, string?> _getCountryCode;
    private readonly Func<bool> _isGeoReady;
    private readonly Func<string, CancellationToken, Task<IPAddress?>> _resolveHost;
    private readonly Func<string, bool> _isProxyFallbackActive;
    private readonly ConcurrentDictionary<string, HostResolutionCacheEntry> _hostResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, StickyRouteDecisionEntry> _stickyRouteDecisions = new(StringComparer.OrdinalIgnoreCase);

    public RouteDecisionService(
        AppConfig config,
        GeoIpService? geoIpService,
        GfwListService? gfwListService,
        Func<string, bool>? isProxyFallbackActive = null)
        : this(
            config,
            domain => gfwListService?.IsInGfwList(domain) == true,
            ip => geoIpService?.GetCountryCode(ip),
            () => geoIpService?.IsInitialized == true,
            ResolveHostAsync,
            isProxyFallbackActive)
    {
    }

    internal RouteDecisionService(
        AppConfig config,
        Func<string, bool>? isInGfwList = null,
        Func<IPAddress, string?>? getCountryCode = null,
        Func<bool>? isGeoReady = null,
        Func<string, CancellationToken, Task<IPAddress?>>? resolveHost = null,
        Func<string, bool>? isProxyFallbackActive = null)
    {
        _config = config;
        _probeDirectDomainSuffixes = BuildDomainSuffixSet(_config.Route.ProbeDirectDomains);
        _proxyDomainSuffixes = BuildDomainSuffixSet(_config.Route.ProxyDomains);
        _directDomainSuffixes = BuildDomainSuffixSet(_config.Route.DirectDomains);
        _isInGfwList = isInGfwList ?? (_ => false);
        _getCountryCode = getCountryCode ?? (_ => null);
        _isGeoReady = isGeoReady ?? (() => getCountryCode != null);
        _resolveHost = resolveHost ?? ResolveHostAsync;
        _isProxyFallbackActive = isProxyFallbackActive ?? (_ => false);
    }

    public Task<RouteDecision> DecideForDomainAsync(string host, CancellationToken ct) =>
        DecideAsync(host, destinationIp: null, resolveHost: true, ct);

    /// <summary>
    /// Makes a routing decision for a packet arriving from the TUN device.
    /// Pass <c>null</c> for <paramref name="destinationIp"/> when the raw destination address
    /// is not meaningful (e.g. a fake-IP pool address) so that routing relies on domain-based
    /// checks and real IP resolution rather than the virtual address.
    /// </summary>
    public Task<RouteDecision> DecideForTunAsync(string? host, IPAddress? destinationIp, CancellationToken ct) =>
        DecideAsync(host, destinationIp, resolveHost: true, ct);

    public Task<RouteDecision> DecideForObservedAddressAsync(string? host, IPAddress destinationIp, CancellationToken ct) =>
        DecideAsync(host, destinationIp, resolveHost: false, ct);

    /// <summary>
    /// Tries to make a routing decision from the domain name alone, without requiring a real IP.
    /// Returns a decision when the result is conclusive (GFW list, explicit config, sticky cache,
    /// or global route mode). Returns <c>null</c> when a real IP is needed for a GeoIP decision.
    /// </summary>
    public RouteDecision? TryDecideWithoutIp(string host)
    {
        var domain = NormalizeDomain(host);
        if (domain == null)
        {
            return null;
        }

        if (IsProbeDomain(domain))
        {
            return RouteDecision.Direct("ProbeDomain", domain, null);
        }

        if (IsProxyDomain(domain))
        {
            return RouteDecision.Proxy("ProxyDomain", domain, null);
        }

        if (_config.Route.EnableGfwList && _isInGfwList(domain))
        {
            return RouteDecision.Proxy("GFW", domain, null);
        }

        if (IsDirectDomain(domain))
        {
            return RouteDecision.Direct("DirectDomain", domain, null);
        }

        if (_isProxyFallbackActive(domain))
        {
            return RouteDecision.Proxy("DirectFailedFallback", domain, null);
        }

        if (TryGetStickyDecision(domain, out var sticky))
        {
            return sticky;
        }

        if (IsGlobalRouteMode())
        {
            return RouteDecision.Proxy("Global", domain, null);
        }

        return null;
    }

    private async Task<RouteDecision> DecideAsync(
        string? host,
        IPAddress? destinationIp,
        bool resolveHost,
        CancellationToken ct)
    {
        var domain = NormalizeDomain(host);
        var canUseStickyDecision = domain != null && destinationIp == null;
        if (domain != null && IsProbeDomain(domain))
        {
            return RouteDecision.Direct("ProbeDomain", domain, destinationIp);
        }

        if (domain != null)
        {
            if (IsProxyDomain(domain))
            {
                return RouteDecision.Proxy("ProxyDomain", domain, destinationIp);
            }

            if (_config.Route.EnableGfwList && _isInGfwList(domain))
            {
                var gfwDecision = RouteDecision.Proxy("GFW", domain, destinationIp);
                if (canUseStickyDecision)
                {
                    CacheStickyDecision(domain, gfwDecision);
                }

                return gfwDecision;
            }

            if (IsDirectDomain(domain))
            {
                return RouteDecision.Direct("DirectDomain", domain, destinationIp);
            }

            if (_isProxyFallbackActive(domain))
            {
                return RouteDecision.Proxy("DirectFailedFallback", domain, destinationIp);
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

        if (canUseStickyDecision && TryGetStickyDecision(domain!, out var stickyDecision))
        {
            return stickyDecision;
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
                    var resolvedDecision = resolvedShouldProxy
                        ? RouteDecision.Proxy($"Geo:{resolvedCountry}", domain, resolvedIp)
                        : RouteDecision.Direct($"Geo:{resolvedCountry}", domain, resolvedIp);
                    if (ShouldCacheStickyDecision(canUseStickyDecision, resolvedDecision))
                    {
                        CacheStickyDecision(domain, resolvedDecision);
                    }

                    return resolvedDecision;
                }
            }

            var shouldProxy = ShouldProxyByGeo(country, _config.Route.GeoProxy, _config.Route.GeoDirect);
            var geoDecision = shouldProxy
                ? RouteDecision.Proxy(country == null ? "GeoUnknown" : $"Geo:{country}", domain, geoIp)
                : RouteDecision.Direct(country == null ? "GeoUnknown" : $"Geo:{country}", domain, geoIp);
            if (ShouldCacheStickyDecision(canUseStickyDecision, geoDecision))
            {
                CacheStickyDecision(domain, geoDecision);
            }

            return geoDecision;
        }

        var defaultDecision = RouteDecision.Proxy("Default", domain, destinationIp);
        if (ShouldCacheStickyDecision(canUseStickyDecision, defaultDecision))
        {
            CacheStickyDecision(domain, defaultDecision);
        }

        return defaultDecision;
    }

    private bool IsProbeDomain(string domain) =>
        IsDomainInSuffixSet(domain, _probeDirectDomainSuffixes);

    private bool IsProxyDomain(string domain) =>
        IsDomainInSuffixSet(domain, _proxyDomainSuffixes);

    private bool IsDirectDomain(string domain) =>
        IsDomainInSuffixSet(domain, _directDomainSuffixes);

    private static bool IsDomainInSuffixSet(string domain, IEnumerable<string> suffixes) =>
        suffixes.Any(suffix =>
            domain.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith('.' + suffix, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> BuildDomainSuffixSet(IEnumerable<string> values) =>
        new(
            values
                .Select(static value => value.Trim().TrimEnd('.').ToLowerInvariant())
                .Where(static value => value.Length > 0),
            StringComparer.OrdinalIgnoreCase);

    private bool TryGetStickyDecision(string domain, out RouteDecision decision)
    {
        decision = default!;
        if (!_stickyRouteDecisions.TryGetValue(domain, out var cached))
        {
            return false;
        }

        if (cached.ExpiresUtc <= DateTime.UtcNow)
        {
            _stickyRouteDecisions.TryRemove(domain, out _);
            return false;
        }

        decision = cached.ShouldProxy
            ? RouteDecision.Proxy($"Sticky:{cached.Reason}", domain, cached.EvaluatedIp)
            : RouteDecision.Direct($"Sticky:{cached.Reason}", domain, cached.EvaluatedIp);
        return true;
    }

    private void CacheStickyDecision(string? domain, RouteDecision decision)
    {
        if (domain == null)
        {
            return;
        }

        _stickyRouteDecisions[domain] = new StickyRouteDecisionEntry(
            decision.ShouldProxy,
            decision.Reason,
            decision.EvaluatedIp,
            DateTime.UtcNow.Add(StickyDecisionTtl));
    }

    private static bool ShouldCacheStickyDecision(bool canUseStickyDecision, RouteDecision decision) =>
        canUseStickyDecision &&
        !decision.Reason.Equals("GeoUnknown", StringComparison.OrdinalIgnoreCase) &&
        !decision.Reason.Equals("Default", StringComparison.OrdinalIgnoreCase);

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

internal sealed record StickyRouteDecisionEntry(bool ShouldProxy, string Reason, IPAddress? EvaluatedIp, DateTime ExpiresUtc);

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
