# TunProxy.NET

.NET 8 实现的 TUN 模式代理，支持 SOCKS5 / HTTP 代理，内置 GEO 直连/代理规则，支持 Native AOT 单文件发布。

**适用场景**：希望在 Windows 上快速获得「全局/智能路由」的 TUN 代理能力，无需复杂配置。

---

## 亮点特性

- ✅ **TUN 虚拟网卡**：基于 Wintun，启动时自动创建、配置网关/路由，并为代理服务器添加 /32 绕行路由避免回环。
- ✅ **多协议代理**：支持 SOCKS5（含账号密码）和 HTTP CONNECT。
- ✅ **智能路由**：优先 GFWList（预留），其次 GEO 规则；GEO 数据库自动下载，失败时默认走代理更安全。直连流量通过独立连接管理器直连目标，避免误代理。
- ✅ **TCP 健壮性**：处理 TCP SYN/SYN-ACK/RST，连接池复用，重传并发连接加锁避免 995 异常。
- ✅ **诊断与指标**：Serilog 控制台 + 文件日志；统计发送/接收字节、连接数、失败数、原始/过滤包等。
- ✅ **Native AOT**：支持 win-x64 AOT 发布，内置动态依赖声明以保证 GeoIP 在裁剪后仍可正常加载。
- ✅ **一键依赖**：启动自动下载 wintun.dll，自动提权（管理员），自动禁用 TUN 口 IPv6。

---

## 快速开始

1) **下载构建**  
从 GitHub Actions 下载最新 `TunProxy.NET-win-x64-AOT.zip`。

2) **解压并运行（管理员）**

```powershell
.\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5
```

- 首次运行会自动下载 `wintun.dll` 并配置路由。  
- 默认读取/生成 `tunproxy.json`，命令行参数可覆盖其中设置。

---

## 配置

### 配置文件 `tunproxy.json`

```json
{
  "Proxy": {
    "Host": "127.0.0.1",
    "Port": 7890,
    "Type": "Socks5",
    "Username": null,
    "Password": null
  },
  "Route": {
    "GeoProxy": ["US", "GB"],
    "GeoDirect": ["CN"],
    "GeoIpDbPath": "GeoLite2-Country.mmdb",
    "EnableGfwList": false,
    "GfwListUrl": "https://raw.githubusercontent.com/gfwlist/gfwlist/master/gfwlist.txt",
    "GfwListPath": "gfwlist.txt",
    "TunRouteMode": "global",
    "AutoAddDefaultRoute": true
  },
  "Logging": {
    "MinimumLevel": "Information",
    "FilePath": "logs/tunproxy-.log"
  }
}
```

- **路由顺序**：GFWList（开启时） > GEO 规则 > 默认走代理。  
- **GEO 默认策略**：无法识别国家时走代理（安全优先）。  
- **直连流量**：使用独立连接管理器直连目标，避免进入代理链路。

### 命令行覆盖

```powershell
.\TunProxy.CLI.exe -p 192.168.1.100:8080 -t http
.\TunProxy.CLI.exe -p 127.0.0.1:7890 -u user -w pass
```

---

## 运行与日志

- 日志：控制台 + `logs/tunproxy-YYYYMMDD.log`（按日滚动）。  
- 指标：每 30 秒输出一次，包含发送/接收字节、活跃/总连接、失败计数、原始包/IPv6/解析失败/直连路由/DNS 等。
- TCP：收到 SYN 自动回复 SYN-ACK；连接失败会回 RST，客户端可快速察觉。

---

## 常见问题

- **GeoIP 下载/加载失败**：手动下载 `GeoLite2-Country.mmdb` 放到程序目录，确保文件名匹配；AOT 发布已声明动态依赖，避免裁剪构造函数。  
- **代理服务器不可达**：检查 `tunproxy.json` 或启动参数；程序启动时会为代理主机添加绕行路由，确保不走 TUN。  
- **IPv6 干扰**：程序启动会在 TUN 口禁用 IPv6；若仍看到大量 IPv6 包，可在系统网络适配器中确认已禁用。  
- **权限问题**：需要管理员启动以创建/配置 TUN 和路由；未提权将自动尝试提升。

---

## 开发与构建

- 运行测试：`dotnet test`
- 调试运行：`dotnet run --project src/TunProxy.CLI/TunProxy.CLI.csproj -- -p 127.0.0.1:7890 -t socks5`
- AOT 发布：参见 `AOT-PUBLISH.md`（win-x64、单文件、裁剪）。

---

## 项目结构

```
TunProxy.NET/
├── src/
│   ├── TunProxy.Core/         # Wintun P/Invoke、IP/TCP/UDP 解析、连接管理
│   ├── TunProxy.Proxy/        # Socks5/HTTP 客户端
│   └── TunProxy.CLI/          # TUN 主程序、路由/Geo/GFW、日志
├── tests/TunProxy.Tests/      # xUnit 单元测试
├── README.md / STATUS.md      # 文档
├── AOT-PUBLISH.md             # AOT 发布指南
└── tunproxy.json              # 示例配置
```

## 许可证

MIT
