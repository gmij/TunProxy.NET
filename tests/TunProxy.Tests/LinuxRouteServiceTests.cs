using System.Net;
using TunProxy.CLI;

namespace TunProxy.Tests;

#pragma warning disable CA1416

public class LinuxRouteServiceTests
{
    [Fact]
    public void ParseOriginalDefaultRoute_SkipsTunDefaultRoute()
    {
        var route = LinuxRouteService.ParseOriginalDefaultRoute(
            """
            default dev tun0 metric 1
            default via 192.0.2.1 dev eth0 proto dhcp metric 100
            """,
            "tun0",
            "10.255.0.1");

        Assert.NotNull(route);
        Assert.Equal("192.0.2.1", route.Gateway);
        Assert.Equal("eth0", route.Device);
    }

    [Fact]
    public void ParseOriginalDefaultRoute_SupportsOnLinkDefaultRoute()
    {
        var route = LinuxRouteService.ParseOriginalDefaultRoute(
            "default dev eth0 scope link metric 100",
            "tun0",
            "10.255.0.1");

        Assert.NotNull(route);
        Assert.Null(route.Gateway);
        Assert.Equal("eth0", route.Device);
        Assert.Equal("dev eth0", route.ToRouteTarget());
    }

    [Fact]
    public void DiscoverActiveManagementClientAddresses_UsesSshEnvAndEstablishedManagementPorts()
    {
        var addresses = LinuxRouteService.DiscoverActiveManagementClientAddresses(
            "198.51.100.10 53344 10.0.0.5 22",
            "198.51.100.10 53344 22",
            """
            0 0 10.0.0.5:22 198.51.100.11:53345
            0 0 10.0.0.5:50000 198.51.100.12:53346
            0 0 10.0.0.5:443 198.51.100.13:53347
            0 0 [2001:db8::1]:22 [2001:db8::2]:53348
            """);

        Assert.Equal(
            [
                IPAddress.Parse("198.51.100.10"),
                IPAddress.Parse("198.51.100.11"),
                IPAddress.Parse("198.51.100.12")
            ],
            addresses);
    }

    [Fact]
    public void AddDefaultRoute_AddsManagementBypassRoutesBeforeTunDefaultRoute()
    {
        var commands = new List<string>();
        var service = new LinuxRouteService(
            "10.255.0.1",
            "255.255.255.0",
            "tun0",
            (file, args) =>
            {
                commands.Add($"{file} {args}");
                return args switch
                {
                    "route show default table main" => (0, "default via 192.0.2.1 dev eth0 metric 100"),
                    "-Htn state established" => (0, "0 0 10.0.0.5:22 198.51.100.11:53345"),
                    "-Hunp" => (0, "UNCONN 0 0 0.0.0.0:9994 0.0.0.0:* users:((\"zerotier-one\",pid=99,fd=3))"),
                    _ => (0, string.Empty)
                };
            },
            name => name == "SSH_CONNECTION"
                ? "198.51.100.10 53344 10.0.0.5 22"
                : null);

        var added = service.AddDefaultRoute();

        Assert.True(added);
        Assert.Contains("ip route replace 198.51.100.10/32 via 192.0.2.1 dev eth0", commands);
        Assert.Contains("ip route replace 198.51.100.11/32 via 192.0.2.1 dev eth0", commands);
        Assert.Contains("ip rule add pref 90 ipproto udp sport 9993 table main", commands);
        Assert.Contains("ip rule add pref 90 ipproto udp sport 9994 table main", commands);
        Assert.Contains("ip route replace default dev tun0 table 51821", commands);
        Assert.Contains("ip rule add pref 100 table main suppress_prefixlength 0", commands);
        Assert.Equal("ip rule add pref 101 not fwmark 0x5450 table 51821", commands[^1]);
    }

    [Fact]
    public void AddDefaultRoute_PreservesZeroTierRouteForActiveManagementClient()
    {
        var commands = new List<string>();
        var service = new LinuxRouteService(
            "10.255.0.1",
            "255.255.255.0",
            "tun0",
            (file, args) =>
            {
                commands.Add($"{file} {args}");
                return args switch
                {
                    "route show default table main" => (0, "default via 192.0.2.1 dev eth0 metric 100"),
                    "route get 10.144.20.50 mark 0x5450" => (0, "10.144.20.50 dev ztzxudepi2 src 10.144.20.200 uid 0"),
                    "-Htn state established" => (0, string.Empty),
                    "-Hunp" => (0, string.Empty),
                    _ => (0, string.Empty)
                };
            },
            name => name == "SSH_CONNECTION"
                ? "10.144.20.50 53344 10.144.20.200 22"
                : null);

        var added = service.AddDefaultRoute();

        Assert.True(added);
        Assert.Contains("ip route replace 10.144.20.50/32 dev ztzxudepi2", commands);
        Assert.DoesNotContain("ip route replace 10.144.20.50/32 via 192.0.2.1 dev eth0", commands);
    }

    [Fact]
    public void AddBypassRoute_PreservesDestinationSpecificRouteForZeroTierProxy()
    {
        var commands = new List<string>();
        var service = new LinuxRouteService(
            "10.255.0.1",
            "255.255.255.0",
            "tun0",
            (file, args) =>
            {
                commands.Add($"{file} {args}");
                return args switch
                {
                    "route get 10.144.20.222 mark 0x5450" => (0, "10.144.20.222 dev ztzxudepi2 src 10.144.20.200 uid 0"),
                    "route show default table main" => (0, "default via 192.0.2.1 dev eth0 metric 100"),
                    _ => (0, string.Empty)
                };
            },
            _ => null);

        var added = service.AddBypassRoute("10.144.20.222");

        Assert.True(added);
        Assert.Contains("ip route replace 10.144.20.222/32 dev ztzxudepi2", commands);
        Assert.DoesNotContain("ip route replace 10.144.20.222/32 via 192.0.2.1 dev eth0", commands);
    }

    [Fact]
    public void AddBypassRoute_FallsBackToOriginalDefaultRouteWhenRouteGetFindsTun()
    {
        var commands = new List<string>();
        var service = new LinuxRouteService(
            "10.255.0.1",
            "255.255.255.0",
            "tun0",
            (file, args) =>
            {
                commands.Add($"{file} {args}");
                return args switch
                {
                    "route get 203.0.113.10 mark 0x5450" => (0, "203.0.113.10 dev tun0 src 10.255.0.1 uid 0"),
                    "route show default table main" => (0, "default via 192.0.2.1 dev eth0 metric 100"),
                    _ => (0, string.Empty)
                };
            },
            _ => null);

        var added = service.AddBypassRoute("203.0.113.10");

        Assert.True(added);
        Assert.Contains("ip route replace 203.0.113.10/32 via 192.0.2.1 dev eth0", commands);
    }

    [Fact]
    public void RemoveDefaultRoute_RemovesPolicyRouteState()
    {
        var commands = new List<string>();
        var service = new LinuxRouteService(
            "10.255.0.1",
            "255.255.255.0",
            "tun0",
            (file, args) =>
            {
                commands.Add($"{file} {args}");
                return args == "-Hunp"
                    ? (0, "UNCONN 0 0 0.0.0.0:9994 0.0.0.0:* users:((\"zerotier-one\",pid=99,fd=3))")
                    : (0, string.Empty);
            },
            _ => null);

        var removed = service.RemoveDefaultRoute();

        Assert.True(removed);
        Assert.Contains("ip rule del pref 101 not fwmark 0x5450 table 51821", commands);
        Assert.Contains("ip rule del pref 100 table main suppress_prefixlength 0", commands);
        Assert.Contains("ip rule del pref 90 ipproto udp sport 9993 table main", commands);
        Assert.Contains("ip rule del pref 90 ipproto udp sport 9994 table main", commands);
        Assert.Contains("ip route flush table 51821", commands);
    }

    [Fact]
    public void DiscoverZeroTierUdpPorts_IncludesDefaultAndObservedPorts()
    {
        var ports = LinuxRouteService.DiscoverZeroTierUdpPorts(
            """
            UNCONN 0 0 0.0.0.0:9994 0.0.0.0:* users:(("zerotier-one",pid=99,fd=3))
            UNCONN 0 0 0.0.0.0:5353 0.0.0.0:* users:(("other",pid=100,fd=3))
            """);

        Assert.Equal([9993, 9994], ports);
    }

    [Fact]
    public void ParseRouteGetTarget_SkipsTunRouteAndUsesOnLinkDevice()
    {
        var route = LinuxRouteService.ParseRouteGetTarget(
            "10.144.20.222 dev ztzxudepi2 src 10.144.20.200 uid 0",
            "tun0",
            "10.255.0.1");

        Assert.NotNull(route);
        Assert.Null(route.Gateway);
        Assert.Equal("ztzxudepi2", route.Device);
        Assert.Equal("dev ztzxudepi2", route.ToRouteTarget());

        Assert.Null(LinuxRouteService.ParseRouteGetTarget(
            "203.0.113.10 dev tun0 src 10.255.0.1 uid 0",
            "tun0",
            "10.255.0.1"));
    }
}

#pragma warning restore CA1416
