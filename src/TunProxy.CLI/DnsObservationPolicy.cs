using System.Net;

namespace TunProxy.CLI;

internal static class DnsObservationPolicy
{
    public static bool ShouldPublishAddress(IPAddress address) =>
        !FakeIpPool.IsFakeIp(address);
}
