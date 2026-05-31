# TunProxy.NET

[English](README.en.md)

TunProxy.NET 是一个基于 .NET 8 的代理运行时，支持本地代理模式和 TUN 透明代理模式。它把系统或应用流量转发到上游 SOCKS5/HTTP 代理，并结合 GFWList、GeoIP、DNS/FakeIP 缓存和直连绕行状态做路由决策。

项目当前提供三类入口：

- Web 控制台：静态 Vue global + Ant Design 页面，用于状态观察、配置、DNS 诊断和实时日志。
- CLI：适合 Linux、macOS 或无桌面环境，可初始化配置、检查资源并后台运行服务。
- Windows 托盘：负责打开控制台、启动/停止运行时、安装/卸载 `TunProxyService`，以及协调保存配置后的重启。

## 界面预览

Web 控制台默认访问地址为 `http://localhost:50000/`。

![Status](docs/images/status.png)

![Config](docs/images/config.png)

![DNS](docs/images/dns.png)

![Logs](docs/images/logs.png)

## 功能概览

- 本地代理模式：监听本机端口，可使用 PAC 或全局系统代理方式接入浏览器和系统流量。
- TUN 透明代理：通过 Wintun 接管 TCP 流量，配置虚拟网卡、默认路由、代理服务器绕行路由和 DNS 转发。
- 上游代理：支持 SOCKS5、HTTP CONNECT，以及可选用户名密码认证。
- 代理可用性检查：通过当前上游代理检查 Google、GitHub、YouTube，帮助确认规则资源下载和路由前置条件。
- 智能路由：GFWList 优先，私有地址和明确直连地址直连，GeoIP 可按国家或地区判断，未知目标默认代理。
- DNS 与 FakeIP：TUN 模式下可返回 `198.18.0.0/16` FakeIP，并在后台解析真实地址，用于稳定地把连接映射回域名。
- 规则资源管理：GeoIP 必须能被 MaxMind 读取，GFWList 必须能解析。启用资源缺失或无效时，会保留本地代理设置模式，不会直接进入不完整的 TUN。
- PAC 支持：生成 `/proxy.pac`，支持复制、预览、应用和清除系统 PAC。
- 日志和指标：控制台日志、文件日志、内存日志 API、连接数、吞吐、DNS 查询、TUN 包处理和失败计数。
- 中英文界面：Web 控制台可在简体中文和英文之间切换。

## 快速开始

### Windows 托盘推荐流程

先发布到 `dist`，再运行托盘程序：

```powershell
.\publish.bat
.\dist\TunProxy.Tray.exe
```

托盘程序会轮询 `http://localhost:50000/api/status`，并负责：

- 启动或停止 `TunProxy.CLI.exe`。
- 安装或卸载 `TunProxyService` Windows 服务。
- 在 Web 控制台请求重启后，等待旧运行时停止，再启动新进程。
- 本地代理模式下应用 PAC 或全局系统代理，TUN 模式下撤销系统代理。

推荐在控制台中按这个顺序配置：

1. 在 Config 页填写上游代理地址、端口、类型和认证信息。
2. 点击代理检查，确认三个检查目标是否可访问。
3. 启用或禁用 GFWList、GeoIP，并准备规则资源。
4. 选择系统代理模式：PAC、Global、TUN 或 None。
5. 保存并重启。TUN 模式在 Windows 上建议由托盘安装服务后运行。

### 开发运行

```powershell
dotnet run --project src\TunProxy.CLI\TunProxy.CLI.csproj -- --proxy 127.0.0.1:7890 --type socks5
```

常用启动参数：

```text
--proxy, -p      上游代理地址，例如 127.0.0.1:7890
--type, -t       上游代理类型：socks5 或 http
--username, -u   上游代理用户名
--password, -w   上游代理密码
--api-host       API 监听地址：localhost、0.0.0.0 或指定 IP
--background     Linux/macOS 后台运行
--install        Windows: 安装服务，并将 tun.enabled 写为 true
--uninstall      Windows: 卸载服务，并将 tun.enabled 写为 false
```

Windows 默认只监听 `127.0.0.1:50000`。Linux/macOS 默认监听 `0.0.0.0:50000`，也可以通过 `--api-host` 显式指定。

### Linux 或无桌面环境

可以直接在终端初始化、查看和修改 `tunproxy.json`：

```bash
./TunProxy.CLI config wizard
./TunProxy.CLI --api-host 0.0.0.0 --background
```

`config wizard` 默认走 HTTP 上游代理、TUN 模式、启用 GFWList/GeoIP 资源准备，并会在保存后执行代理检查和资源准备。

常用命令：

```text
config path      输出当前配置文件路径
config show      以 JSON 方式显示当前配置
config init      初始化配置文件
config set       通过命令行参数更新配置
config wizard    交互式配置，并自动检查代理和准备资源
resource status  查看 GFWList/GeoIP 资源状态
resource prepare 准备已启用资源
resource prepare geo|gfw|all
```

示例：

```bash
./TunProxy.CLI config set --proxy 127.0.0.1:7890 --type http --mode tun --fake-ip
./TunProxy.CLI resource prepare
```

## Web 控制台

### Status

状态页展示运行状态、当前模式、上游代理、活动连接、运行时间、系统代理模式、FakeIP 状态和最近 TCP 连接失败原因。它每 5 秒刷新，并保存 30 分钟滚动流量样本，显示发送/接收曲线、吞吐率、总连接数、失败连接数，以及 TUN 包处理诊断。

页面操作：

- Restart service：写入重启请求。
- Stop service：写入停止请求。
- 连接失败提示：根据代理拒绝、连接失败、DNS 失败等类型给出排查方向。

### Config

配置页按三步组织：

1. 上游代理：主机、端口、类型、可选用户名密码。
2. 路由资源：启用 GFWList/GeoIP，查看资源是否 ready，单独下载或统一准备。
3. 系统代理模式：PAC、Global、TUN、None，并可设置本地代理端口。

右侧面板展示代理检查结果、资源状态、当前配置摘要和 PAC 操作。保存配置会写入 `tunproxy.json`，再通过 `tunproxy.restart` 交给托盘或服务协调重启。

### DNS

DNS 页只在 TUN 模式下显示完整数据。本地代理模式下，页面会提示 DNS 拦截不可用。

TUN 模式下，DNS 页会按域名聚合解析记录，展示：

- 域名、IP 列表、命中次数和最后活动时间。
- 路由结果：DIRECT、PROXY 或未知。
- 路由原因：GFW、Geo、Default、私有地址或 FakeIP 相关状态。
- DNS 缓存标记，以及按记录清理缓存的操作。

### Logs

日志页每 2 秒拉取内存日志，最多保留 1000 行，默认过滤连接日志。它支持：

- All、Connections、DNS、Warnings、Errors 分段筛选。
- 自定义文本筛选。
- 暂停/继续、清空、滚动到最新。
- 侧栏统计当前日志、警告、连接和错误数量。

## 运行模式

### Local Proxy

`tun.enabled = false` 时进入本地代理模式。运行时监听 `localProxy.listenPort`，然后根据 `localProxy.systemProxyMode` 决定是否修改系统代理：

- `pac`：系统使用 `http://127.0.0.1:50000/proxy.pac`。
- `global`：系统代理直接指向本地代理端口。
- `none`：只启动本地代理，不修改系统代理。

这个模式不需要管理员权限，适合先完成上游代理检查、规则资源下载和 PAC 配置。

### TUN

`tun.enabled = true` 或 `localProxy.systemProxyMode = "tun"` 时进入 TUN 模式。Windows 下需要管理员权限或 Windows 服务。运行时会创建 Wintun 适配器，配置 TUN 地址、默认路由、代理服务器绕行路由和 DNS 转发。

TUN 模式默认启用 FakeIP。DNS 服务为 A 记录返回 `198.18.0.0/16` 地址，TUN 层再把 FakeIP 连接还原到域名和真实地址，避免路由决策丢失域名上下文。

如果启用了 GeoIP 或 GFWList，但资源缺失或无效，程序会保留本地代理设置模式。修复资源后，再保存配置并重启进入 TUN。

## 配置文件

配置文件为程序目录下的 `tunproxy.json`。Web 控制台和 `config` 命令都会更新此文件。

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

## 路由决策

智能路由大致按以下顺序执行：

1. 命中探测域名或私有地址时直连。
2. 域名命中 `proxyDomains` 或 GFWList 时走代理。
3. 域名命中 `directDomains` 时直连。
4. TUN + FakeIP 场景下，先把 FakeIP 映射回域名，再等待后台真实 DNS 结果。
5. 直连域名在短时间内多次连接失败时，会临时升级为代理并在 TTL 到期后恢复尝试直连。
6. 启用 GeoIP 且数据库可用时，根据 `geoDirect` 和 `geoProxy` 判断。
7. 没有可用规则或目标不可识别时，默认走代理。

这套策略优先保证可用性和避免静默直连泄漏：未知目标默认代理，私有地址默认直连。

## HTTP API

Web 控制台使用的主要 API：

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
│  ├─ TunProxy.Core/       共享配置、连接、指标、服务、平台辅助和 Wintun 抽象
│  ├─ TunProxy.CLI/        运行时、HTTP API、Web 控制台、本地代理、TUN、DNS、规则资源
│  ├─ TunProxy.Tray/       Windows 托盘、服务生命周期、重启编排、系统代理集成
│  └─ TunProxy.Tray.macOS/ macOS 托盘实验项目
├─ tests/TunProxy.Tests/   xUnit 测试
├─ docs/images/            README 截图
├─ publish.bat             本地 Windows 发布脚本
└─ TunProxy.NET.sln
```

## 常见问题

### 为什么保存配置后需要重启？

上游代理、TUN 适配器、DNS 服务、路由和监听端口都是运行时状态。保存配置只负责写入 `tunproxy.json`，真正生效需要重启。Web 控制台写入 `tunproxy.restart`，托盘或服务在旧运行时停止后再启动新进程，避免端口、路由和驱动占用竞争。

### 为什么 TUN 没有直接启动？

如果启用了 GeoIP 或 GFWList，程序会检查对应文件是否真的有效。文件存在但无法读取、无法解析时仍会阻止直接进入 TUN，并保留本地代理设置模式。请在 Config 页准备资源后保存并重启。

### DNS 页面为什么没有数据？

DNS 页面主要展示 TUN 模式下的 DNS 拦截、FakeIP 和路由记录。本地代理模式下，浏览器通常把域名交给本地代理和上游代理，不需要 TUN DNS 拦截。

### 什么时候需要管理员权限？

本地代理、PAC 和全局系统代理模式不需要管理员权限。TUN 模式需要创建虚拟网卡并修改系统路由，Windows 下建议通过托盘安装服务，或以管理员运行 CLI。

## 许可

MIT
