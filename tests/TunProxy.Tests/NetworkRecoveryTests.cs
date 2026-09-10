using System.Net;
using TunProxy.CLI;
using TunProxy.Core.Connections;
using TunProxy.Core.Packets;

namespace TunProxy.Tests;

public class NetworkRecoveryTests
{
    [Fact]
    public void SourceChange_UpdatesNewConnectionsButPreservesExistingTcpState()
    {
        var oldSource = IPAddress.Parse("10.0.0.10");
        var newSource = IPAddress.Parse("192.168.50.10");
        using var manager = new TcpConnectionManager("10.144.20.200", 3222, ProxyType.Http, bindAddress: oldSource);
        var firstPacket = Packet(10000);
        var existing = manager.GetOrCreateConnection(firstPacket)!;
        manager.UpdateBindAddress(newSource);
        var next = manager.GetOrCreateConnection(Packet(10001))!;
        Assert.Same(existing, manager.GetOrCreateConnection(firstPacket));
        Assert.Equal(oldSource, existing.BindAddress);
        Assert.Equal(newSource, next.BindAddress);
    }

    private static IPPacket Packet(ushort port) => IPPacket.Parse(PacketBuilder.BuildTcpPacketRaw(
        [10, 255, 0, 1], [203, 0, 113, 1], port, 443, PacketBuilder.TcpFlags.SYN, 1, 0))!;

    [Fact]
    public void SecondaryNetworkTakesOver_WithoutOwningOrDeletingItsSystemRoute()
    {
        var network = new Network();
        var service = network.CreateService();
        service.RefreshRouteState();
        Assert.True(service.AddBypassRoute("192.168.50.80"));
        var version = service.NetworkVersion;
        network.AddWifi();

        service.RefreshRouteState();

        Assert.True(service.NetworkVersion > version);
        Assert.DoesNotContain(network.Routes, route => route.Network == "192.168.50.80");
        Assert.Contains(network.Routes, route => route.Network == "192.168.50.0");
        Assert.Equal(IPAddress.Parse("192.168.50.10"), service.GetLocalAddressForDestination(IPAddress.Parse("192.168.50.80")));
        var commands = network.Commands.Count;
        service.ClearAllBypassRoutes();
        Assert.Equal(commands, network.Commands.Count);
        version = service.NetworkVersion;
        service.RefreshRouteState();
        Assert.Equal(version, service.NetworkVersion);
    }

    [Fact]
    public void MissingOwnedRoute_IsRestoredWithoutOtherNetworkChanges()
    {
        var network = new Network();
        var service = network.CreateService();
        service.RefreshRouteState();
        Assert.True(service.AddBypassRoute("203.0.113.1"));
        network.Routes.RemoveAll(route => route.Network == "203.0.113.1");
        service.RefreshRouteState();
        Assert.Contains(network.Routes, route => route.Network == "203.0.113.1");
    }

    [Fact]
    public void FailedDeletion_IsRetriedEvenWhenTopologyDoesNotChangeAgain()
    {
        var network = new Network();
        var service = network.CreateService();
        service.RefreshRouteState();
        Assert.True(service.AddBypassRoute("192.168.50.80"));
        network.AddWifi();
        network.FailDelete = true;
        service.RefreshRouteState();
        Assert.Contains(network.Routes, route => route.Network == "192.168.50.80");
        network.FailDelete = false;
        service.RefreshRouteState();
        Assert.DoesNotContain(network.Routes, route => route.Network == "192.168.50.80");
    }

    [Fact]
    public void FormerRemoteTargetBecomesLocal_RebuildAndCleanupNeverDeleteIt()
    {
        var network = new Network();
        var service = network.CreateService();
        service.RefreshRouteState();
        Assert.True(service.AddBypassRoute("192.168.50.10"));
        network.Routes.RemoveAll(route => route.Network == "192.168.50.10");
        network.AddWifi();
        network.Routes.Add(Network.Route("192.168.50.10", "255.255.255.255", "On-link", "192.168.50.10"));
        network.Commands.Clear();
        service.RefreshRouteState();
        service.ClearAllBypassRoutes();
        Assert.Empty(network.Commands);
        Assert.Contains(network.Routes, route => route.Network == "192.168.50.10");
    }

    [Fact]
    public async Task MonitorRetriesAfterException_AndStopWaitsForActiveRefresh()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var monitor = new NetworkStateMonitor(async _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new InvalidOperationException("transient");
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None, subscribe: false, interval: TimeSpan.FromMilliseconds(10));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = monitor.DisposeAsync().AsTask();
            Assert.False(stopping.IsCompleted);
            release.SetResult();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, attempts);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class Network
    {
        public List<RouteEntry> Routes { get; } = [Route("0.0.0.0", "0.0.0.0", "10.0.0.1", "10.0.0.10")];
        public List<OnLinkRouteCandidate> Interfaces { get; } =
            [new("Ethernet", 3, IPAddress.Parse("10.0.0.10"), IPAddress.Parse("255.255.255.0"))];
        public List<string> Commands { get; } = [];
        public bool FailDelete { get; set; }
        public WindowsRouteService CreateService() => new(() => Routes.ToList(), () => Interfaces.ToList(), Run);
        public void AddWifi()
        {
            Interfaces.Add(new("Wi-Fi", 7, IPAddress.Parse("192.168.50.10"), IPAddress.Parse("255.255.255.0")));
            Routes.Add(Route("192.168.50.0", "255.255.255.0", "On-link", "192.168.50.10"));
        }
        private (int, string) Run(string executable, string command)
        {
            Commands.Add(command);
            Assert.Equal("netsh", executable);
            var parts = command.Split(' ');
            var ip = parts[4].Split('/')[0];
            if (parts[2] == "delete")
            {
                Assert.Contains("interface=3", command);
                Assert.Contains("nexthop=10.0.0.1", command);
                if (FailDelete) return (1, "busy");
                Routes.RemoveAll(route => route.Network == ip && route.Interface == "10.0.0.10");
            }
            else
                Routes.Add(Route(ip, "255.255.255.255", "10.0.0.1", "10.0.0.10"));
            return (0, "");
        }
        public static RouteEntry Route(string network, string mask, string gateway, string local) => new()
        { Network = network, Netmask = mask, Gateway = gateway, Interface = local, Metric = "25" };
    }
}
