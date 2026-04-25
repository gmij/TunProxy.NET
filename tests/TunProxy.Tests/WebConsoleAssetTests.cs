using System.Text.RegularExpressions;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class WebConsoleAssetTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string SourceRoot = Path.Combine(RepositoryRoot, "src", "TunProxy.CLI", "wwwroot");

    [Fact]
    public void SourceWebRootFiles_AreEmbeddedInCliAssembly()
    {
        var sourceFiles = EnumerateRelativeFiles(SourceRoot);
        var embeddedFiles = EmbeddedWebConsoleAssets
            .FromAssembly(typeof(ApiEndpoints).Assembly)
            .Paths
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(sourceFiles, embeddedFiles);
    }

    [Theory]
    [InlineData("index.html", "status-page.js")]
    [InlineData("config.html", "config-page.js")]
    [InlineData("dns.html", "dns-page.js")]
    [InlineData("logs.html", "logs-page.js")]
    public void Pages_LoadSharedAndPageScriptsWithoutInlineScript(string htmlFile, string pageScript)
    {
        var html = File.ReadAllText(Path.Combine(SourceRoot, htmlFile));

        Assert.Contains("<script src=\"/i18n.js\"></script>", html);
        Assert.Contains("<script src=\"/nav.js\"></script>", html);
        Assert.Contains("<script src=\"/api.js\"></script>", html);
        Assert.Contains($"<script src=\"/{pageScript}\"></script>", html);
        Assert.DoesNotContain("<script>\r\n", html);
        Assert.DoesNotContain("<script>\n", html);
    }

    [Fact]
    public void HtmlI18nAttributes_ResolveToFrontendCatalogKeys()
    {
        var keys = GetHtmlI18nKeys();
        var catalog = TunProxy.Core.Localization.LocalizedText.GetFrontendCatalog("en");

        var missing = keys
            .Where(key => !catalog.ContainsKey(key))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("index.html", "status-badges")]
    [InlineData("config.html", "check-proxy-button")]
    [InlineData("dns.html", "dns-table")]
    [InlineData("logs.html", "log-box")]
    public void Pages_ContainExpectedSmokeElements(string htmlFile, string elementId)
    {
        var html = File.ReadAllText(Path.Combine(SourceRoot, htmlFile));

        Assert.Contains($"id=\"{elementId}\"", html);
        Assert.Contains("data-page-title-key=", html);
    }

    private static IReadOnlyList<string> EnumerateRelativeFiles(string root)
    {
        return Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("status-page.js", "text/javascript")]
    [InlineData("app.css", "text/css")]
    [InlineData("favicon.png", "image/png")]
    public void EmbeddedAssets_OpenWithExpectedContentType(string path, string expectedContentType)
    {
        var assets = EmbeddedWebConsoleAssets.FromAssembly(typeof(ApiEndpoints).Assembly);

        Assert.True(assets.TryOpen(path, out var stream, out var contentType));
        using (stream)
        {
            Assert.True(stream.Length > 0);
        }

        Assert.StartsWith(expectedContentType, contentType, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetHtmlI18nKeys()
    {
        var regex = new Regex("data-i18n(?:-[a-z]+)?=\"([^\"]+)\"");
        return Directory
            .EnumerateFiles(SourceRoot, "*.html")
            .SelectMany(path => regex.Matches(File.ReadAllText(path)))
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TunProxy.NET.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
