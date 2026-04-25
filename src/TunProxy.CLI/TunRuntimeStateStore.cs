using System.Net;
using System.Text.Json;
using Serilog;

namespace TunProxy.CLI;

internal sealed class TunRuntimeStateStore
{
    private readonly string _path;

    public TunRuntimeStateStore(string? path = null)
    {
        _path = path ?? AppPaths.RuntimeStateFilePath;
    }

    public IPAddress? LoadLastOutboundBindAddress()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, AppJsonContext.Default.TunRuntimeState);
            return IPAddress.TryParse(state?.LastOutboundBindAddress, out var address)
                ? address
                : null;
        }
        catch (Exception ex)
        {
            Log.Debug("[TUN ] Failed to read runtime state: {Message}", ex.Message);
            return null;
        }
    }

    public void SaveLastOutboundBindAddress(IPAddress? address)
    {
        if (address == null)
        {
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var state = new TunRuntimeState
            {
                LastOutboundBindAddress = address.ToString(),
                LastOutboundBindAddressUtc = DateTime.UtcNow
            };
            File.WriteAllText(_path, JsonSerializer.Serialize(state, AppJsonContext.Default.TunRuntimeState));
        }
        catch (Exception ex)
        {
            Log.Debug("[TUN ] Failed to write runtime state: {Message}", ex.Message);
        }
    }
}

public sealed class TunRuntimeState
{
    public string? LastOutboundBindAddress { get; set; }
    public DateTime? LastOutboundBindAddressUtc { get; set; }
}
