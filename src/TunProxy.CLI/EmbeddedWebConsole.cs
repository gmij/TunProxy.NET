using System.Reflection;

namespace TunProxy.CLI;

internal static class EmbeddedWebConsole
{
    public static WebApplication UseEmbeddedWebConsole(this WebApplication app)
    {
        var assets = EmbeddedWebConsoleAssets.FromAssembly(typeof(EmbeddedWebConsole).Assembly);

        app.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) &&
                !HttpMethods.IsHead(context.Request.Method))
            {
                await next();
                return;
            }

            var path = NormalizeRequestPath(context.Request.Path.Value);
            if (!assets.TryOpen(path, out var stream, out var contentType))
            {
                await next();
                return;
            }

            await using (stream)
            {
                context.Response.ContentType = contentType;
                if (stream.CanSeek)
                {
                    context.Response.ContentLength = stream.Length;
                }

                if (HttpMethods.IsHead(context.Request.Method))
                {
                    return;
                }

                await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
        });

        return app;
    }

    private static string NormalizeRequestPath(string? value)
    {
        var path = string.IsNullOrWhiteSpace(value) || value == "/"
            ? "index.html"
            : value.TrimStart('/').Replace('\\', '/');

        return path.Contains("..", StringComparison.Ordinal)
            ? string.Empty
            : path;
    }
}

internal sealed class EmbeddedWebConsoleAssets
{
    private const string ResourcePrefix = "wwwroot/";

    private static readonly IReadOnlyDictionary<string, string> ContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".css"] = "text/css; charset=utf-8",
            [".html"] = "text/html; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".png"] = "image/png",
            [".txt"] = "text/plain; charset=utf-8"
        };

    private readonly Assembly _assembly;
    private readonly IReadOnlyDictionary<string, string> _resourcesByPath;

    private EmbeddedWebConsoleAssets(Assembly assembly, IReadOnlyDictionary<string, string> resourcesByPath)
    {
        _assembly = assembly;
        _resourcesByPath = resourcesByPath;
    }

    public IReadOnlyCollection<string> Paths => _resourcesByPath.Keys.ToArray();

    public static EmbeddedWebConsoleAssets FromAssembly(Assembly assembly)
    {
        var resources = assembly
            .GetManifestResourceNames()
            .Where(static name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            .ToDictionary(
                static name => name[ResourcePrefix.Length..].Replace('\\', '/'),
                static name => name,
                StringComparer.OrdinalIgnoreCase);

        return new EmbeddedWebConsoleAssets(assembly, resources);
    }

    public bool TryOpen(string path, out Stream stream, out string contentType)
    {
        stream = Stream.Null;
        contentType = "application/octet-stream";

        if (!_resourcesByPath.TryGetValue(path, out var resourceName))
        {
            return false;
        }

        var resourceStream = _assembly.GetManifestResourceStream(resourceName);
        if (resourceStream == null)
        {
            return false;
        }

        stream = resourceStream;
        contentType = ResolveContentType(path);
        return true;
    }

    private static string ResolveContentType(string path)
    {
        return ContentTypes.TryGetValue(Path.GetExtension(path), out var contentType)
            ? contentType
            : "application/octet-stream";
    }
}
