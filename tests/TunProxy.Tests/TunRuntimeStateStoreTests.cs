using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

public class TunRuntimeStateStoreTests
{
    [Fact]
    public void SaveAndLoadLastOutboundBindSelection_PreservesLinkSnapshot()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-state-{Guid.NewGuid():N}.json");
        try
        {
            var store = new TunRuntimeStateStore(path);
            var bindAddress = IPAddress.Parse("10.144.20.210");
            var proxyAddress = IPAddress.Parse("10.144.20.200");
            var netmask = IPAddress.Parse("255.255.255.0");

            store.SaveLastOutboundBindSelection(new OutboundBindSelection(
                bindAddress,
                OutboundBindAddressSource.LocalSubnet,
                ProxyAddress: proxyAddress,
                Netmask: netmask));

            var state = store.LoadLastOutboundBindState();

            Assert.NotNull(state);
            Assert.Equal(bindAddress, state.Value.Address);
            Assert.Equal(proxyAddress, state.Value.ProxyAddress);
            Assert.Equal(netmask, state.Value.Netmask);
            Assert.Equal(OutboundBindAddressSource.LocalSubnet, state.Value.Source);
            Assert.NotNull(state.Value.SavedUtc);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadLastOutboundBindState_ReadsLegacyAddressOnlyState()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunproxy-state-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{\"lastOutboundBindAddress\":\"10.144.20.210\"}");
            var store = new TunRuntimeStateStore(path);

            var state = store.LoadLastOutboundBindState();

            Assert.NotNull(state);
            Assert.Equal(IPAddress.Parse("10.144.20.210"), state.Value.Address);
            Assert.Null(state.Value.ProxyAddress);
            Assert.Null(state.Value.Netmask);
            Assert.Null(state.Value.Source);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
