# TunProxy.NET

[中文](README.md)

TunProxy.NET is a .NET 8 proxy runtime built for local proxy and TUN transparent proxy workflows where operators need visibility, configuration, and diagnostics in one place. It forwards system or application traffic to an upstream SOCKS5/HTTP proxy, then makes routing decisions with GFWList, GeoIP, FakeIP DNS, manual domain rules, and direct-failure fallback policies.

The current project has three user surfaces:

- Web console: static Vue global + Ant Design pages organized around five runtime workflows: Status, Config, Rules, DNS, and Logs.
- CLI: useful on Linux, macOS, or headless hosts for initializing config, checking resources, and running in the background.
- Windows tray: opens the console, starts/stops the runtime, installs/uninstalls `TunProxyService`, and coordinates restarts after config saves.

The current Web console uses a sidebar-driven operations layout:

- Status: runtime health, connection summary, traffic trends, and TUN diagnostics.
- Config: upstream proxy, routing resources, and system proxy mode in one save/restart flow.
- Rules: manual proxy/direct domain lists plus direct-failure fallback tuning.
- DNS: DNS records, route reasons, cache state, and clear actions in TUN mode.
- Logs: live in-memory logs with quick filters for connections, DNS, warnings, and errors.

## Screenshots

The Web console is available at `http://localhost:50000/` by default.

### Status

![Status](docs/images/status.png)

### Config

![Config](docs/images/config.png)

### Rules

![Rules](docs/images/rules.png)

### DNS

![DNS](docs/images/dns.png)

### Logs

![Logs](docs/images/logs.png)

## Features

- Operations-oriented Web console: sidebar navigation, summary cards, side panels, language switcher, and responsive layouts for long-running monitoring.
- Local proxy mode: listens on a local port and can connect browsers/system traffic through PAC or global system proxy settings.
- TUN transparent proxy: captures TCP traffic through Wintun, with virtual adapter setup, default route setup, upstream-proxy bypass route, and DNS forwarding.
- Upstream proxy support: SOCKS5, HTTP CONNECT, and optional username/password authentication.
- Upstream health check: runs Google, GitHub, and YouTube checks directly from the Config page before depending on rule resources.
- Smart routing: GFWList first, private and explicit direct addresses direct, GeoIP by country/region when enabled, and unknown destinations proxy by default.
- Manual routing rules: a dedicated Rules page manages `proxyDomains`, `directDomains`, and direct-failure fallback policy.
- DNS and FakeIP: in TUN mode, A records can receive `198.18.0.0/16` fake IPs while the runtime resolves real addresses in the background and maps TCP connections back to domains.
- Rule resource management: GeoIP must be readable by MaxMind, and GFWList must be parseable. Missing or invalid enabled resources keep the app in local-proxy setup mode instead of starting incomplete TUN routing.
- PAC support: serves `/proxy.pac` and supports copy, preview, apply, and clear system PAC actions.
- Logs and metrics: the Status page summarizes traffic and diagnostics, while the Logs page provides live memory-log streaming, filters, pause, clear, and scroll-to-latest actions.
- Bilingual console: simplified Chinese and English can be switched in the Web console.

## Quick Start

### Recommended Windows Tray Flow

Publish to `dist`, then run the tray app:

```powershell
.\publish.bat
.\dist\TunProxy.Tray.exe
```

The tray app polls `http://localhost:50000/api/status` and handles:

- Starting or stopping `TunProxy.CLI.exe`.
- Installing or uninstalling the `TunProxyService` Windows service.
- Waiting for the old runtime to stop before starting a replacement after the Web console requests a restart.
- Applying PAC/global system proxy settings in local proxy mode and disabling system proxy in TUN mode.

Recommended console setup order:

1. Open the Config page and enter upstream host, port, type, and optional credentials.
2. Run the upstream proxy check and confirm the three targets are reachable.
3. Enable or disable GFWList and GeoIP, then prepare rule resources.
4. If you need fixed proxy domains, direct domains, or direct-failure fallback tuning, continue in the Rules page.
5. Return to the Config page and choose the system proxy mode: PAC, Global, TUN, or None.
6. Save and restart. On Windows, run TUN mode through the tray-installed service when possible.

### Development Run

```powershell
dotnet run --project src\TunProxy.CLI\TunProxy.CLI.csproj -- --proxy 127.0.0.1:7890 --type socks5
```

Common startup arguments:

```text
--proxy, -p      Upstream proxy endpoint, for example 127.0.0.1:7890
--type, -t       Upstream proxy type: socks5 or http
--username, -u   Upstream proxy username
--password, -w   Upstream proxy password
--api-host       API listen host: localhost, 0.0.0.0, or a specific IP
--background     Run in the background on Linux/macOS
--install        Windows: install the service and set tun.enabled to true
--uninstall      Windows: uninstall the service and set tun.enabled to false
```

Windows listens on `127.0.0.1:50000` by default. Linux/macOS listens on `0.0.0.0:50000` by default. You can override either behavior with `--api-host`.

### Linux or Headless Hosts

You can initialize, inspect, and update `tunproxy.json` from the terminal:

```bash
./TunProxy.CLI config wizard
./TunProxy.CLI --api-host 0.0.0.0 --background
```

`config wizard` defaults to HTTP upstream proxy, TUN mode, and enabled GFWList/GeoIP preparation. After saving config, it runs the upstream proxy check and prepares enabled resources.

Common commands:

```text
config path      Print the current config file path
config show      Print the current config as JSON
config init      Create the config file
config set       Update config values from CLI flags
config wizard    Run interactive setup, proxy check, and resource preparation
resource status  Show GFWList/GeoIP resource status
resource prepare Prepare enabled resources
resource prepare geo|gfw|all
```

Example:

```bash
./TunProxy.CLI config set --proxy 127.0.0.1:7890 --type http --mode tun --fake-ip
./TunProxy.CLI resource prepare
```

## Web Console

### Status

The Status page is now organized like an operations dashboard: a top status band for runtime state, mode tags, proxy summary, and service actions; traffic cards and trend history in the middle; and a dedicated TUN diagnostics column on the right. It refreshes every 5 seconds and keeps rolling traffic samples for sent/received trend rendering.

Page actions:

- Restart service: writes a restart request.
- Stop service: writes a stop request.
- Connection issue hint: classifies proxy denial, connect failures, DNS failures, and generic failures.

### Config

The Config page follows a three-step workflow:

1. Upstream proxy: host, port, type, and optional username/password.
2. Routing resources: enable GFWList/GeoIP, inspect readiness, download one resource, or prepare all enabled resources.
3. System proxy mode: PAC, Global, TUN, or None, plus the local proxy port.

The side panel shows a config summary plus PAC address and quick actions. Saving config writes `tunproxy.json`, then creates `tunproxy.restart` so the tray or service can coordinate restart.

### Rules

The Rules page separates manual routing policy from the basic connection setup. It is dedicated to:

- `proxyDomains`: domains that must always use the proxy.
- `directDomains`: domains that must always go direct.
- Direct-failure fallback: failure threshold, observation window, and temporary proxy TTL.

This keeps day-to-day configuration simpler while making route tuning easier to adjust independently.

### DNS

The DNS page shows full data only in TUN mode. In local proxy mode it explicitly shows that DNS interception is unavailable; in TUN mode it adds summary cards, a grouped records table, route legends, and side explanations.

In TUN mode, the DNS page groups records by domain and shows:

- Domain, IP list, seen count, and last active time.
- Route result: DIRECT, PROXY, or unknown.
- Route reason: GFW, Geo, Default, private address, or FakeIP-related state.
- DNS cache markers and per-record cache clear actions.

### Logs

The Logs page polls in-memory logs every 2 seconds, keeps up to 1000 lines, and defaults to connection logs. The layout combines a terminal-style stream with a side summary panel and supports:

- All, Connections, DNS, Warnings, and Errors segmented filters.
- Custom text filter.
- Pause/resume, clear, and scroll to latest.
- Side statistics for current logs, warnings, connections, and errors.

## Runtime Modes

### Local Proxy

When `tun.enabled = false`, TunProxy runs in local proxy mode. The runtime listens on `localProxy.listenPort`, then uses `localProxy.systemProxyMode` to decide whether to modify system proxy settings:

- `pac`: system PAC points to `http://127.0.0.1:50000/proxy.pac`.
- `global`: system proxy points directly to the local proxy port.
- `none`: local proxy runs without changing system proxy settings.

Linux/macOS do not provide one system proxy switch that applies to every process, so the Web Console only shows PAC and Global on Windows. On Linux/macOS, use TUN for transparent system-wide capture; local proxy mode is still useful for clients that explicitly set `http_proxy`, `https_proxy`, or `curl -x`.

This mode does not require administrator privileges and is useful for upstream checks, rule resource downloads, and PAC setup.

### TUN

When `tun.enabled = true` or `localProxy.systemProxyMode = "tun"`, TunProxy runs in TUN mode. On Windows this requires administrator privileges or the Windows service. The runtime creates a Wintun adapter, configures the TUN address, default route, upstream-proxy bypass route, and DNS forwarding.

FakeIP is enabled by default in TUN mode. The DNS service returns addresses from `198.18.0.0/16` for A records, and the TUN layer maps those fake-IP connections back to domain names and real addresses so routing does not lose domain context.

On Linux, TUN full-capture mode uses a dedicated routing table plus `fwmark` policy routing. Existing non-default routes in the main table, such as LAN, ZeroTier, Docker, and cloud private routes, keep priority; ordinary default traffic enters `tun0`. TunProxy's own upstream proxy, direct-connect, DNS, and DoH sockets are marked so they return through the original main route instead of looping back into TUN. Common ZeroTier UDP ports are also kept on the main route, which lets ZeroTier remain the underlay network while TunProxy performs split routing.

If GeoIP or GFWList is enabled but missing or invalid, TunProxy stays in local-proxy setup mode. Prepare or repair resources, then save config and restart into TUN.

## Configuration

The configuration file is `tunproxy.json` in the application directory. The Config and Rules pages in the Web console, plus the CLI `config` commands, all update the same file.

```json
{
  "proxy": {
    "host": "127.0.0.1",
    "port": 7890,
    "type": "Socks5",
    "username": null,
    "password": null
  },
  "tun": {
    "enabled": false,
    "ipAddress": "10.255.0.1",
    "subnetMask": "255.255.255.0",
    "addDefaultRoute": true,
    "dnsServer": "8.8.8.8",
    "fakeIpMode": true
  },
  "localProxy": {
    "listenPort": 8080,
    "setSystemProxy": false,
    "systemProxyMode": "none",
    "bypassList": "<local>;localhost;127.0.0.1;10.*;192.168.*",
    "systemProxyBackup": {
      "captured": false,
      "proxyEnable": 0,
      "proxyServer": null,
      "proxyOverride": null,
      "autoConfigUrl": null
    }
  },
  "route": {
    "mode": "smart",
    "proxyDomains": [],
    "directDomains": [],
    "probeDirectDomains": [],
    "enableGeo": true,
    "geoProxy": [],
    "geoDirect": [],
    "geoIpDbPath": "GeoLite2-Country.mmdb",
    "enableGfwList": true,
    "gfwListUrl": "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt",
    "gfwListPath": "gfwlist.txt",
    "enableDirectFailureFallback": true,
    "directFailureThreshold": 3,
    "directFailureWindowSeconds": 300,
    "directFailureFallbackTtlSeconds": 900,
    "tunRouteMode": "global",
    "tunRouteApps": [],
    "autoAddDefaultRoute": true
  },
  "logging": {
    "minimumLevel": "Information",
    "filePath": "logs/tunproxy-.log"
  }
}
```

## Routing

Smart routing works together with the manual domain lists managed on the Rules page, roughly in this order:

1. Probe domains and private addresses go direct.
2. Domains in `proxyDomains` or matching GFWList use the proxy.
3. Domains in `directDomains` go direct.
4. In TUN + FakeIP scenarios, fake IPs are mapped back to domains before waiting for real DNS results.
5. Direct domains that fail repeatedly within the configured window can be temporarily upgraded to proxy routing until the fallback TTL expires.
6. When GeoIP is enabled and the database is ready, `geoDirect` and `geoProxy` are evaluated.
7. If no usable rule matches or the target cannot be identified, proxy is the default.

The policy favors reachability and leak avoidance: unknown destinations default to proxy, while private addresses default to direct.

## HTTP API

Main APIs used by the Web console:

Note: the Rules page does not have a separate API surface. Like the Config page, it reads and writes the shared configuration through `GET/POST /api/config`; the workflow is split in the UI, not in the API.

```text
GET    /api/status
GET    /api/config
POST   /api/config
POST   /api/restart
POST   /api/service/restart
POST   /api/service/stop
POST   /api/upstream-proxy/check
GET    /api/rule-resources/status
POST   /api/rule-resources/prepare
POST   /api/rule-resources/download?resource=geo|gfw
GET    /api/dns-records
DELETE /api/dns-cache?ip=...&domain=...
GET    /api/diagnostics/tun
GET    /api/logs?after=...
GET    /api/i18n?culture=zh-CN|en
GET    /proxy.pac
POST   /api/set-pac
POST   /api/clear-pac
POST   /api/enable-tun
```

## Build and Test

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
```

Local publish:

```powershell
.\publish.bat
```

`publish.bat` publishes Windows x64 self-contained single-file binaries to `dist` and copies `wintun.dll`.

## Project Layout

```text
TunProxy.NET/
├─ src/
│  ├─ TunProxy.Core/       Shared config, connections, metrics, service helpers, platform helpers, Wintun abstractions
│  ├─ TunProxy.CLI/        Runtime, HTTP API, Web console, local proxy, TUN, DNS, rule resources
│  ├─ TunProxy.Tray/       Windows tray, service lifecycle, restart orchestration, system proxy integration
│  └─ TunProxy.Tray.macOS/ Experimental macOS tray project
├─ tests/TunProxy.Tests/   xUnit tests
├─ docs/images/            README screenshots
├─ publish.bat             Local Windows publish script
└─ TunProxy.NET.sln
```

## FAQ

### Why does saving configuration restart the service?

The upstream proxy, TUN adapter, DNS service, routes, and listening ports are runtime state. Saving from either the Config page or the Rules page only writes `tunproxy.json`; applying it requires restart. The Web console creates `tunproxy.restart`, and the tray or service starts a replacement only after the old runtime has stopped, avoiding port, route, and driver ownership races.

### Why did TUN not start directly?

When GeoIP or GFWList is enabled, TunProxy verifies that the corresponding files are actually valid. A file that exists but cannot be read or parsed still blocks direct TUN startup and keeps local-proxy setup mode. Prepare resources from the Config page, optionally adjust manual routing in the Rules page, then save and restart.

### Why is the DNS page empty?

The DNS page mainly shows TUN DNS interception, FakeIP, and routing records. In local proxy mode, browsers usually hand hostnames to the local/upstream proxy, so TUN DNS interception is not needed.

### When are administrator privileges required?

Local proxy, PAC, and global system proxy modes do not require administrator privileges. TUN mode creates a virtual adapter and changes system routes, so on Windows you should install the service from the tray app or run the CLI as administrator.

## License

MIT
