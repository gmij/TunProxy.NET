using System.Diagnostics;
using System.Net;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

internal static class UpstreamProxyHealthChecker
{
    private static readonly UpstreamProxyHealthTarget[] DefaultTargets =
    [
        new("Google", "https://www.google.com/"),
        new("GitHub", "https://github.com/"),
        new("YouTube", "https://www.youtube.com/")
    ];

    public static async Task<UpstreamProxyStatus> CheckAsync(
        ProxyConfig proxyConfig,
        CancellationToken ct)
    {
        if (ProxyHttpClientFactory.BuildProxyUri(proxyConfig) == null)
        {
            return BuildInvalidProxyStatus();
        }

        using var client = ProxyHttpClientFactory.Create(proxyConfig, TimeSpan.FromSeconds(10));
        return await CheckAsync(
            (request, token) => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token),
            ct);
    }

    internal static async Task<UpstreamProxyStatus> CheckAsync(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        CancellationToken ct)
    {
        var results = new List<UpstreamProxyTargetStatus>(DefaultTargets.Length);

        foreach (var target in DefaultTargets)
        {
            results.Add(await CheckTargetAsync(target, sendAsync, ct));
        }

        return new UpstreamProxyStatus
        {
            IsAvailable = results.All(static result => result.IsOk),
            CheckedAtUtc = DateTime.UtcNow,
            Targets = results
        };
    }

    private static async Task<UpstreamProxyTargetStatus> CheckTargetAsync(
        UpstreamProxyHealthTarget target,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
            request.Headers.UserAgent.ParseAdd("TunProxy/1.0");

            using var response = await sendAsync(request, ct);
            stopwatch.Stop();

            return new UpstreamProxyTargetStatus
            {
                Name = target.Name,
                Url = target.Url,
                StatusCode = (int)response.StatusCode,
                IsOk = response.StatusCode == HttpStatusCode.OK,
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new UpstreamProxyTargetStatus
            {
                Name = target.Name,
                Url = target.Url,
                IsOk = false,
                Error = $"{ex.GetType().Name}: {ex.Message}",
                ElapsedMs = stopwatch.ElapsedMilliseconds
            };
        }
    }

    private static UpstreamProxyStatus BuildInvalidProxyStatus() =>
        new()
        {
            IsAvailable = false,
            CheckedAtUtc = DateTime.UtcNow,
            Targets = DefaultTargets
                .Select(static target => new UpstreamProxyTargetStatus
                {
                    Name = target.Name,
                    Url = target.Url,
                    IsOk = false,
                    Error = "Invalid upstream proxy endpoint."
                })
                .ToList()
        };

    private sealed record UpstreamProxyHealthTarget(string Name, string Url);
}

public sealed class UpstreamProxyStatus
{
    public bool IsAvailable { get; set; }
    public DateTime CheckedAtUtc { get; set; }
    public List<UpstreamProxyTargetStatus> Targets { get; set; } = [];
}

public sealed class UpstreamProxyTargetStatus
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public int? StatusCode { get; set; }
    public bool IsOk { get; set; }
    public string? Error { get; set; }
    public long ElapsedMs { get; set; }
}
