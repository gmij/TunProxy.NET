using System.Text;
using TunProxy.Core.Configuration;

namespace TunProxy.CLI;

/// <summary>
/// Generates PAC (Proxy Auto-Config) content from the current configuration.
/// </summary>
public static class PacGenerator
{
    public static async Task<string> GenerateAsync(AppConfig config)
    {
        var route = config.Route;
        var proxyStr = $"PROXY 127.0.0.1:{config.LocalProxy.ListenPort}";

        var mode = route.Mode ?? "smart";
        var defaultStr = mode.Equals("blacklist", StringComparison.OrdinalIgnoreCase) ||
                         mode.Equals("whitelist", StringComparison.OrdinalIgnoreCase)
            ? "DIRECT"
            : proxyStr;

        var directDomains = new HashSet<string>(
            route.DirectDomains ?? [],
            StringComparer.OrdinalIgnoreCase);
        var proxyDomains = new HashSet<string>(
            route.ProxyDomains ?? [],
            StringComparer.OrdinalIgnoreCase);

        var gfwListPath = AppPathResolver.ResolveAppFilePath(route.GfwListPath);
        if (route.EnableGfwList && !string.IsNullOrEmpty(route.GfwListPath) && File.Exists(gfwListPath))
        {
            var gfw = await LoadGfwDomainsAsync(gfwListPath);
            foreach (var domain in gfw)
            {
                proxyDomains.Add(domain);
            }
        }

        return Build(proxyStr, defaultStr, directDomains, proxyDomains, mode);
    }

    private static async Task<HashSet<string>> LoadGfwDomainsAsync(string path)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var base64 = await File.ReadAllTextAsync(path);
            var bytes = Convert.FromBase64String(base64.Trim());
            var content = Encoding.UTF8.GetString(bytes);

            foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var text = line.Trim();
                if (text.StartsWith('!') || text.StartsWith('[') || text.StartsWith("@@"))
                {
                    continue;
                }

                string? domain = null;
                if (text.StartsWith("||"))
                {
                    domain = text.TrimStart('|').Split('^')[0];
                }
                else if (!text.StartsWith('|') && !text.Contains('*') && !text.Contains('/'))
                {
                    domain = text;
                }

                if (!string.IsNullOrEmpty(domain))
                {
                    result.Add(domain.ToLowerInvariant());
                }
            }
        }
        catch
        {
        }

        return result;
    }

    private static string Build(
        string proxyStr,
        string defaultStr,
        HashSet<string> directDomains,
        HashSet<string> proxyDomains,
        string mode)
    {
        var builder = new StringBuilder(proxyDomains.Count * 30 + 1024);

        builder.AppendLine($"// TunProxy PAC - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"// Mode: {mode} | Direct: {directDomains.Count} | Proxy: {proxyDomains.Count}");
        builder.AppendLine();
        builder.AppendLine($"var P=\"{proxyStr}\",D=\"DIRECT\";");
        builder.AppendLine("function m(h,d){return h===d||(h.length>d.length&&'.'===h[h.length-d.length-1]&&h.slice(-d.length)===d);}");
        builder.AppendLine();

        AppendDomainArray(builder, "var dd=", directDomains);
        AppendDomainArray(builder, "var pd=", proxyDomains);

        builder.AppendLine();
        builder.AppendLine("function FindProxyForURL(url,host){");
        builder.AppendLine("  var h=host.split(':')[0].toLowerCase();");
        if (directDomains.Count > 0)
        {
            builder.AppendLine("  for(var i=0;i<dd.length;i++)if(m(h,dd[i]))return D;");
        }
        if (proxyDomains.Count > 0)
        {
            builder.AppendLine("  for(var i=0;i<pd.length;i++)if(m(h,pd[i]))return P;");
        }
        builder.AppendLine($"  return {(defaultStr == "DIRECT" ? "D" : "P")};");
        builder.AppendLine("}");

        return builder.ToString();
    }

    private static void AppendDomainArray(StringBuilder builder, string varDecl, HashSet<string> domains)
    {
        builder.Append(varDecl);
        builder.Append('[');

        var first = true;
        foreach (var domain in domains)
        {
            if (!first)
            {
                builder.Append(',');
            }

            builder.Append('"');
            builder.Append(domain.Replace("\"", "\\\"", StringComparison.Ordinal));
            builder.Append('"');
            first = false;
        }

        builder.AppendLine("];");
    }
}
