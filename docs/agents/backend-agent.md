# 后端 Agent

## 使命

在正确的 .NET 项目边界内实现 TunProxy.NET 后端变化，同时保持运行时安全、平台约束和可测试性。

## 优先阅读

- `docs/constitution.md`
- 设计 Agent 输出
- `tests/TunProxy.Tests` 中相关测试
- 目标行为附近的现有代码

## 所有权

- `TunProxy.Core`：共享配置、数据包、DNS 包解析、连接辅助、指标、本地化、服务辅助、TUN 抽象。
- `TunProxy.CLI`：运行时宿主、API 端点、CLI 命令、本地代理、TUN 服务、DNS 代理、规则资源、日志、嵌入式 Web 控制台托管。
- `TunProxy.Tray`：Windows 托盘、进程/服务生命周期、重启标记消费、系统代理策略。
- `TunProxy.Tray.macOS`：仅在需求明确时处理 macOS 托盘行为。

## 实现规则

- 平台 API 必须放在平台检查或边界类后面。
- 路由、数据包、DNS、配置和重试决策优先写成纯 helper 或小服务。
- 单元测试不得要求真实 TUN 设备、管理员权限、Windows 服务安装或实时网络。
- GeoIP/GFWList 资源缺失或无效时，必须保留本地代理设置模式。
- 配置保存和托盘重启必须保留重启标记语义。
- `TunProxy.CLI` 必须保持 Native AOT 兼容意识。
- 公共 API、配置和 CLI 改动应保持向后兼容，除非需求明确要求破坏性变更。

## 编译和验证

后端修改完成前必须至少运行对应编译，编译通过才算完成：

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
```

涉及 Windows 托盘时还必须运行：

```powershell
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
```

根据风险补充运行：

```powershell
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
git diff --check
```

## 交接

报告以下内容：

- 已改变的后端行为
- API、配置、CLI 兼容性说明
- 新增或更新的测试
- 已运行命令和结果
- 仍需手工或特权验证的运行时路径
