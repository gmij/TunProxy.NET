# TunProxy.NET

[English](README.en.md)

TunProxy.NET 是一个基于 .NET 8 的代理工具，提供本地代理模式和 TUN 透明代理模式。它可以把系统或应用流量转发到上游 SOCKS5/HTTP 代理，并通过 GFWList、GeoIP、DNS 缓存和直连绕行规则做智能路由。

当前版本的重点不是只提供一个命令行程序，而是提供一套可观察、可配置、可重启的运行体验：托盘程序负责服务生命周期，Web 控制台负责配置和诊断，TUN 启动会校验必需资源是否真的可用。

## 界面预览

Web 控制台默认运行在 `http://localhost:50000/`。

![Status](docs/images/status.png)

![Config](docs/images/config.png)

![DNS](docs/images/dns.png)

![Logs](docs/images/logs.png)

## 当前能力

- 本地代理模式：监听本机端口，支持自动设置 Windows 系统代理，适合先完成上游代理和规则资源准备。
- TUN 模式：通过 Wintun 虚拟网卡透明接管 TCP 流量，自动配置 TUN 地址、路由和 DNS 转发。
- 上游代理：支持 SOCKS5、HTTP CONNECT，以及可选用户名密码。
- 智能路由：优先匹配 GFWList，再按 GeoIP 判断；私有地址和直连绕行地址直接走原始网关。
- DNS 缓存：TUN 模式下缓存 A 记录，安全命中时直接构造响应，并在 DNS 页面显示、搜索和清理记录。
- 规则资源校验：GeoIP 数据库必须能被 MaxMind 读取，GFWList 必须能解析；缺失或无效时不会直接进入 TUN。
- Web 控制台：提供状态、配置、DNS 记录、直连 IP、实时日志、PAC 预览和系统 PAC 操作。
- 上游代理检查：通过当前代理访问 Google、GitHub、YouTube，三者均返回 HTTP 200 后才解锁路由配置。
- 保存后重启：配置写入 `tunproxy.json` 后通过 `tunproxy.restart` 标记交给托盘程序重启，避免旧进程端口未释放时抢先启动新进程。
- Windows 托盘：启动/停止服务、安装/卸载 Windows 服务、打开控制台、修复 TUN 模式不一致状态。
- 日志与指标：控制台、文件日志、内存日志 API，包含连接、DNS、TUN 包、失败计数和吞吐指标。

## 运行方式

### 推荐：托盘程序

先发布到 `dist`，再运行托盘程序：

```powershell
.\publish.bat
.\dist\TunProxy.Tray.exe
```

托盘程序会轮询 `http://localhost:50000/api/status`，并负责：

- 启动或停止 `TunProxy.CLI.exe`。
- 安装或卸载 `TunProxyService` Windows 服务。
- 当 Web 控制台请求重启时，等待旧服务停止后再重新启动。
- 本地代理模式下应用系统代理，TUN 模式下撤销系统代理。

### 开发运行

```powershell
dotnet run --project src\TunProxy.CLI\TunProxy.CLI.csproj -- --proxy 127.0.0.1:7890 --type socks5
```

常用参数：

```powershell
--proxy, -p      上游代理地址，例如 127.0.0.1:7890
--type, -t       上游代理类型：socks5 或 http
--username, -u   上游代理用户名
--password, -w   上游代理密码
--install        安装 Windows 服务，并将 tun.enabled 写为 true
--uninstall      卸载 Windows 服务，并将 tun.enabled 写为 false
```

启动后打开：

```text
http://localhost:50000/
```

## 模式说明

### Local Proxy 模式

`tun.enabled = false` 时进入本地代理模式。程序监听 `localProxy.listenPort`，可自动把 Windows 系统代理设置到本机端口。这个模式不需要管理员权限，适合完成上游代理检查、GeoIP/GFWList 下载和 PAC 配置。

### TUN 模式

`tun.enabled = true` 时进入 TUN 模式。Windows 下需要管理员权限或以 Windows 服务运行。程序会创建 Wintun 适配器，配置 TUN 地址、默认路由、上游代理绕行路由和 DNS 转发。

如果启用了 GeoIP 或 GFWList，但资源缺失或无效，程序会先进入 Local Proxy setup mode，让你在 Web 控制台下载或修复资源。资源可读取、可解析后，再保存配置并重启进入 TUN。

## 配置文件

配置文件为程序目录下的 `tunproxy.json`。Web 控制台保存配置时会更新此文件。

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
    "ipAddress": "10.0.0.1",
    "subnetMask": "255.255.255.0",
    "addDefaultRoute": true,
    "dnsServer": "8.8.8.8"
  },
  "localProxy": {
    "listenPort": 8080,
    "setSystemProxy": true,
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
    "enableGeo": true,
    "geoProxy": [],
    "geoDirect": [],
    "geoIpDbPath": "GeoLite2-Country.mmdb",
    "enableGfwList": true,
    "gfwListUrl": "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt",
    "gfwListPath": "gfwlist.txt",
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

## 路由决策

智能路由的大致顺序：

1. 如果域名命中 GFWList，走代理。
2. 如果目标 IP 是私有地址，直连。
3. 如果有域名但没有目标 IP，先解析并缓存结果。
4. 如果启用 GeoIP，按 `geoDirect` 和 `geoProxy` 判断国家或地区。
5. 无法识别或没有规则命中时，默认走代理。

这套策略偏向可用性和安全性：未知地址默认代理，私有地址默认直连。

## Web 控制台

主要页面：

- `Status`：运行状态、代理信息、连接数、吞吐量和 TUN 诊断指标。
- `Config`：上游代理、智能路由、GeoIP/GFWList 资源、保存并重启、PAC 操作。
- `DNS`：TUN 模式下的 DNS 解析记录、路由原因、DNS 缓存状态和直连 IP 列表。
- `Logs`：实时内存日志，支持暂停、清空、滚动到底部和按连接/DNS/警告/错误过滤。

主要 API：

```text
GET    /api/status
GET    /api/config
POST   /api/config
POST   /api/restart
POST   /api/upstream-proxy/check
GET    /api/rule-resources/status
POST   /api/rule-resources/prepare
POST   /api/rule-resources/download
GET    /api/dns-records
DELETE /api/dns-cache?ip=...
GET    /api/direct-ips
GET    /api/logs
GET    /proxy.pac
POST   /api/set-pac
POST   /api/clear-pac
```

## 构建与测试

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
```

本地发布：

```powershell
.\publish.bat
```

`publish.bat` 会发布 Windows x64 self-contained 单文件版本到 `dist`，并拷贝 `wintun.dll`。

## 项目结构

```text
TunProxy.NET/
├─ src/
│  ├─ TunProxy.Core/       核心配置、包解析、连接管理、Wintun 抽象
│  ├─ TunProxy.CLI/        Web 控制台、API、本地代理、TUN 服务、路由和 DNS
│  ├─ TunProxy.Tray/       Windows 托盘和服务生命周期管理
│  └─ TunProxy.Tray.macOS/ macOS 托盘实验项目
├─ tests/TunProxy.Tests/   xUnit 测试
├─ docs/images/            README 截图
├─ publish.bat             本地 Windows 发布脚本
└─ TunProxy.NET.sln
```

## 常见问题

### 为什么配置保存后要重启？

上游代理、TUN、路由服务和 DNS 服务在运行时持有连接、路由和监听状态。保存配置只负责写入 `tunproxy.json`，真正生效需要重启服务。Web 控制台会写入 `tunproxy.restart`，由托盘程序在旧服务停止后再启动，避免端口和路由状态竞争。

### 为什么 TUN 没有直接启动？

如果启用了 GeoIP 或 GFWList，程序会检查对应文件是否真的有效。文件存在但不能读取、不能解析时仍会阻止 TUN 启动。请在 Config 页面准备资源后保存并重启。

### DNS 页面为什么没有数据？

DNS 页面只在 TUN 模式下显示数据。本地代理模式下，浏览器会把域名直接交给本地代理和上游代理，不需要 TUN DNS 拦截。

### 什么时候需要管理员权限？

本地代理模式不需要管理员权限。TUN 模式需要创建虚拟网卡和修改系统路由，Windows 下请通过托盘安装服务，或以管理员运行 CLI。

## 许可

MIT
