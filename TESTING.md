# 测试指南

## 运行测试

### 本地运行

```bash
cd TunProxy.NET

# 运行所有测试
dotnet test

# 运行测试并查看详细输出
dotnet test --logger "console;verbosity=detailed"

# 运行测试并生成覆盖率报告
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

### CI/CD 中运行

GitHub Actions 会在每次 push 时自动运行测试。

---

## 测试覆盖

### 单元测试

| 模块 | 测试文件 | 覆盖内容 |
|------|---------|---------|
| **IPPacket** | `IPPacketTests.cs` | IPv4 包解析、TCP/UDP/ICMP识别、错误处理 |
| **Socks5Client** | `ProxyClientTests.cs` | 构造函数、连接异常处理 |
| **HttpProxyClient** | `ProxyClientTests.cs` | 构造函数、连接异常处理 |

### 测试用例

#### IPPacketTests

- ✅ `Parse_ValidIPv4Packet_ReturnsPacket` - 解析有效 IPv4 TCP 包
- ✅ `Parse_InvalidVersion_ReturnsNull` - 拒绝非 IPv4 包
- ✅ `Parse_TooShort_ReturnsNull` - 拒绝过短数据包
- ✅ `Parse_UDPPacket_ReturnsPacketWithUDPHeader` - 解析 UDP 包
- ✅ `Parse_ICMPPacket_ReturnsPacketWithoutPort` - 解析 ICMP 包

#### ProxyClientTests

- ✅ `Constructor_ValidParameters_CreatesClient` - 构造函数测试
- ✅ `Constructor_WithAuth_ValidParameters_CreatesClient` - 带认证的构造函数
- ✅ `ConnectAsync_InvalidProxy_ThrowsException` - 连接失败异常处理

---

## 集成测试（TODO）

后续需要添加：

- [ ] TUN 设备创建和销毁
- [ ] 真实代理服务器连接测试
- [ ] 端到端流量转发测试
- [ ] 性能基准测试

---

## 代码覆盖率目标

- **核心库 (TunProxy.Core):** 80%+
- **代理库 (TunProxy.Proxy):** 70%+
- **CLI (TunProxy.CLI):** 50%+

---

## 调试测试

```bash
# 运行单个测试类
dotnet test --filter "FullyQualifiedName~IPPacketTests"

# 运行单个测试方法
dotnet test --filter "FullyQualifiedName~Parse_ValidIPv4Packet_ReturnsPacket"

# 调试模式（需要附加调试器）
dotnet test --no-build --debug
```

---

## 常见问题

**Q: 测试失败？**
A: 查看详细输出 `dotnet test --logger "console;verbosity=detailed"`

**Q: 需要特殊环境？**
A: 单元测试不需要，但集成测试可能需要 TUN 设备和代理服务器。
