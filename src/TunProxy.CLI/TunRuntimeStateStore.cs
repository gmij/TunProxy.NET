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
        return LoadLastOutboundBindState()?.Address;
    }

    public TunOutboundBindState? LoadLastOutboundBindState()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var state = JsonSerializer.Deserialize(json, AppJsonContext.Default.TunRuntimeState);
            if (!IPAddress.TryParse(state?.LastOutboundBindAddress, out var address))
            {
                return null;
            }

            var proxyAddress = IPAddress.TryParse(state.LastOutboundProxyAddress, out var parsedProxyAddress)
                ? parsedProxyAddress
                : null;
            var netmask = IPAddress.TryParse(state.LastOutboundNetmask, out var parsedNetmask)
                ? parsedNetmask
                : null;
            var source = Enum.TryParse<OutboundBindAddressSource>(state.LastOutboundBindSource, out var parsedSource)
                ? parsedSource
                : (OutboundBindAddressSource?)null;

            return new TunOutboundBindState(
                address,
                proxyAddress,
                netmask,
                source,
                state.LastOutboundBindAddressUtc);
        }
        catch (Exception ex)
        {
            Log.Debug("[TUN ] Failed to read runtime state: {Message}", ex.Message);
            return null;
        }
    }

    public void SaveLastOutboundBindAddress(IPAddress? address)
    {
        SaveLastOutboundBindSelection(new OutboundBindSelection(address, OutboundBindAddressSource.None));
    }

    public void SaveLastOutboundBindSelection(OutboundBindSelection selection)
    {
        if (selection.Address == null)
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
                LastOutboundBindAddress = selection.Address.ToString(),
                LastOutboundBindAddressUtc = DateTime.UtcNow,
                LastOutboundProxyAddress = selection.ProxyAddress?.ToString(),
                LastOutboundNetmask = selection.Netmask?.ToString(),
                LastOutboundBindSource = selection.Source.ToString()
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
    public string? LastOutboundProxyAddress { get; set; }
    public string? LastOutboundNetmask { get; set; }
    public string? LastOutboundBindSource { get; set; }
}
