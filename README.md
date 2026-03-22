# TunProxy.NET

🥔 用 .NET 8 实现的 TUN 模式代理，支持 AOT 编译。

## 功能

- ✅ TUN 虚拟网卡（基于 Wintun）
- ✅ SOCKS5 代理
- ✅ HTTP 代理（CONNECT 方法）
- ✅ Native AOT 支持
- ⏳ TCP 流量转发
- ⏳ 规则引擎
- ⏳ DNS 处理

## 快速开始（傻瓜版）

### 1. 下载编译好的程序

从 [GitHub Actions](https://github.com/gmij/TunProxy.NET/actions) 下载最新构建：
- 点击最新的成功 build
- 下载 `TunProxy.NET-win-x64-AOT.zip`

### 2. 解压并运行

```powershell
# 解压 ZIP 文件
# 以管理员身份运行 PowerShell

# 运行（会自动下载 wintun.dll，自动配置 TUN 接口）
.\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5
```

**就这么简单！** wintun.dll 会自动下载，TUN 接口会自动配置。

```bash
cd TunProxy.NET

# 调试构建
dotnet build

# 发布（普通）
dotnet publish -c Release -r win-x64 --self-contained

# 发布（AOT，单文件）
dotnet publish -c Release -r win-x64 /p:PublishAot=true
```

### 3. 运行

**需要管理员权限！**

```bash
# 使用默认配置（SOCKS5 127.0.0.1:7890）
TunProxy.CLI.exe

# 指定代理
TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5

# HTTP 代理
TunProxy.CLI.exe -p 192.168.1.100:8080 -t http
```

### 4. 配置 TUN 接口

```powershell
# 设置 TUN 接口 IP
netsh interface ip set address "TunProxy" static 10.0.0.1 255.255.255.0

# 添加路由（可选，只代理特定流量）
route add 0.0.0.0 mask 0.0.0.0 10.0.0.1
```

## 项目结构

```
TunProxy.NET/
├── src/
│   ├── TunProxy.Core/      # 核心库
│   │   ├── Wintun/         # Wintun P/Invoke 封装
│   │   └── Packets/        # IP/TCP/UDP包解析
│   ├── TunProxy.Proxy/     # 代理协议实现
│   │   ├── Socks5Client.cs
│   │   └── HttpProxyClient.cs
│   └── TunProxy.CLI/       # 命令行工具
├── wintun.dll              # Wintun 驱动（单独下载）
└── README.md
```

## 工作原理

```
用户应用 → Wintun 虚拟网卡 → TunProxy.NET → SOCKS5/HTTP代理 → 互联网
```

1. Wintun 创建虚拟网卡，拦截系统流量
2. TunProxy 从 TUN 设备读取 IP 包
3. 解析 TCP/UDP 头部，提取目标地址
4. 通过代理服务器转发流量
5. 接收响应，写回 TUN 设备

## 当前状态

**MVP 阶段** - 基础功能已实现，还需要：

- [ ] 完整的 TCP 连接管理（当前是简化版本）
- [ ] 响应数据包写回 TUN 设备
- [ ] DNS 请求处理
- [ ] 规则引擎（决定哪些流量走代理）
- [ ] 性能优化（Span<T>, 零拷贝）
- [ ] Windows 服务支持
- [ ] 配置 GUI

## 技术栈

- .NET 8
- Native AOT
- Wintun (WireGuard 团队)
- P/Invoke

## 许可证

MIT

---

_斌哥的数字分身 🥔 出品_
