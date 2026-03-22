# TunProxy.NET

.NET 8 实现的 TUN 模式代理，支持 SOCKS5 和 HTTP 代理，可 AOT 编译为单文件。

替代目标：Clash for Windows（TUN 模式）

## 快速开始（傻瓜版）

### 1. 下载编译好的程序

从 [GitHub Actions](https://github.com/gmij/TunProxy.NET/actions) 下载最新构建：
- 点击最新的成功 build
- 下载 `TunProxy.NET-win-x64-AOT.zip`

### 2. 解压并运行

```powershell
# 解压 ZIP 文件
# 双击运行（会自动提权）

.\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5
```

就这么简单！wintun.dll 会自动下载，TUN 接口会自动配置，权限会自动提升。

## 功能特性

- [x] TUN 虚拟网卡（基于 Wintun）
- [x] SOCKS5 代理
- [x] HTTP 代理（CONNECT 方法）
- [x] Native AOT 支持
- [x] TCP 长连接管理（连接池复用）
- [x] 响应数据包回写
- [x] 自动下载 wintun.dll
- [x] 自动配置 TUN 接口
- [x] 自动提权（管理员权限）
- [x] Serilog 日志（控制台 + 文件）
- [ ] DNS 请求处理
- [ ] 路由规则引擎

## 命令行参数

| 参数 | 说明 | 默认值 |
|------|------|--------|
| `-p, --proxy <host:port>` | 代理服务器地址 | 127.0.0.1:7890 |
| `-t, --type <type>` | 代理类型：socks5, http | socks5 |
| `-h, --help` | 显示帮助 | - |

## 示例

```powershell
# 使用默认配置（SOCKS5 127.0.0.1:7890）
.\TunProxy.CLI.exe

# 指定 SOCKS5 代理
.\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5

# 指定 HTTP 代理
.\TunProxy.CLI.exe -p 192.168.1.100:8080 -t http
```

## 日志

日志输出到：
- 控制台（实时查看）
- `logs/tunproxy-YYYYMMDD.log`（按天滚动）

## 项目结构

```
TunProxy.NET/
├── src/
│   ├── TunProxy.Core/         # 核心库
│   │   ├── Wintun/            # Wintun P/Invoke 封装
│   │   ├── Packets/           # IP/TCP/UDP包解析
│   │   └── Connections/       # TCP 连接管理
│   ├── TunProxy.Proxy/        # 代理协议实现
│   │   ├── Socks5Client.cs
│   │   └── HttpProxyClient.cs
│   └── TunProxy.CLI/          # 命令行工具
├── tests/
│   └── TunProxy.Tests/        # 单元测试
├── README.md
└── TunProxy.NET.sln
```

## 技术栈

- .NET 8
- Native AOT
- Wintun (WireGuard 团队)
- Serilog
- xUnit

## 注意事项

1. 需要 Windows 10/11
2. 首次运行会自动下载 wintun.dll（约 200KB）
3. 需要稳定的代理服务器

## 许可证

MIT
