# Architecture

TunProxy.NET has three primary surfaces:

- `TunProxy.Core` contains shared configuration, connection, metrics, service, and platform helpers.
- `TunProxy.CLI` hosts the proxy runtime, HTTP API, static web console, DNS proxy, rule resource preparation, and TUN packet pipeline.
- `TunProxy.Tray` owns Windows tray interaction, service control, restart orchestration, browser launch, and system proxy integration.

## Runtime boundaries

`TunProxyService` coordinates runtime lifecycle. Pure decisions are extracted into small helpers so routing behavior can be reviewed and tested without a TUN device:

- outbound bind address selection
- route diagnostics construction
- rule resource initialization and retry
- packet and connection decisions
- TCP payload sequencing
- proxy bypass route configuration
- direct bypass route scheduling
- pending relay cleanup
- response segmentation
- traffic log snapshot construction

`LocalProxyService` and the TUN TCP path share upstream connection behavior through `UpstreamTcpConnector`.

## DNS and routing ownership

`DnsResolutionStore` owns DNS response cache entries and observed hostname snapshots. `IpCacheManager` owns route-oriented IP state, such as direct bypass, proxy-blocked, and connect-failed addresses.

This separation is intentional: DNS observations can influence routing, but IP route state should not become a second DNS cache.

## Restart model

Configuration saves create a restart marker. The tray waits until the old service is stopped before starting a replacement process, which avoids port and driver ownership races.

## Resource setup mode

When GeoIP or GFWList is enabled but the required files are missing or invalid, the app falls back to local-proxy setup mode instead of starting TUN mode with incomplete routing data.
