# TunProxy.NET 项目宪章

## 1. 产品使命

TunProxy.NET 是基于 .NET 8 的代理运行时，支持本地代理模式和 TUN 透明代理模式。项目目标是把系统或应用流量可靠地转发到 SOCKS5/HTTP 上游代理，同时为 CLI、Web 控制台和 Windows 托盘提供清晰、可诊断、可修复、可配置的使用体验。

## 2. 当前系统边界

- `src/TunProxy.Core` 负责共享配置、数据包解析/构造、连接辅助、指标、本地化、Windows 服务辅助和 TUN 抽象。
- `src/TunProxy.CLI` 负责运行时宿主、HTTP API、嵌入式静态 Web 控制台、本地代理模式、TUN 代理模式、DNS 代理、规则资源编排、日志和命令行工作流。
- `src/TunProxy.Tray` 负责 Windows 托盘交互、服务生命周期、重启编排、浏览器打开和 Windows 系统代理策略。
- `src/TunProxy.Tray.macOS` 是独立的 macOS 托盘界面。除非代码和需求明确证明，否则不要假设它与 Windows 托盘功能等价。
- `tests/TunProxy.Tests` 是主要回归测试套件。新的纯逻辑行为通常应在这里覆盖。
- `src/TunProxy.CLI/wwwroot` 是静态嵌入式 Vue global + Ant Design 控制台，目前没有 Node 构建流水线。

## 3. 不可破坏的原则

- GeoIP 或 GFWList 已启用但文件缺失/无效时，必须保留回落到本地代理设置模式的行为。
- 平台相关行为应放在应用边界；路由、DNS、数据包、配置、策略判断应尽量沉到可测试的小类。
- 单元测试不得依赖真实 TUN 设备、管理员权限、Windows 服务安装或实时网络。
- DNS 观测和主机名快照归 `DnsResolutionStore`；面向路由的 IP 状态归 `IpCacheManager`。
- 未知或不安全的路由场景应优先保证可用性和安全代理行为，避免静默直连泄漏。
- 配置写入必须尊重重启标记模型。托盘负责协调进程替换，避免端口和驱动占用竞态。
- `TunProxy.CLI` 必须保持 Native AOT 意识：避免未显式保留或未测试的重反射改动。
- 保护现有 CLI、API、配置文件和 Web 控制台行为，避免意外破坏兼容性。

## 4. 工程标准

- 目标框架为 .NET 8，并启用 nullable reference types。
- 遵守 `.editorconfig`：UTF-8、CRLF、C# 四空格缩进、Markdown/JSON/YAML 两空格缩进。
- 优先使用显式依赖和聚焦的小服务，避免膨胀的静态工具类。
- 优先使用结构化解析和序列化 API，避免脆弱的字符串拼接/拆分。
- 代码修改必须聚焦当前需求，避免无关重构。
- 针对路由决策、配置工作流、DNS 行为、数据包/TCP 决策、资源设置、Web 控制台资源/API 契约添加聚焦测试。
- 无论修改前端还是后端，只要涉及可编译项目，完成前都必须运行对应编译命令；编译通过才算完成。
- 后端或共享逻辑修改至少运行：

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
```

- Windows 托盘修改还必须运行：

```powershell
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
```

- Web 控制台前端修改必须通过嵌入资源所在项目的编译：

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
```

- 测试和收尾优先使用：

```powershell
dotnet restore tests\TunProxy.Tests\TunProxy.Tests.csproj
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
git diff --check
```

## 5. 前端标准

- Web 控制台是运维型工具，不是营销页面。优先保证信息密度、稳定性、可扫描性和操作效率。
- 必须严格遵守当前 Vue + Ant Design 技术栈的最佳实践：组件职责清晰、状态来源明确、表单/表格/反馈组件语义正确、异步状态可见、错误处理完整。
- 保持当前静态架构，除非需求明确批准引入构建系统。
- 优先复用 `api.js`、`i18n.js`、`nav.js`、共享 shell 组件、`console.css` 和既有 Ant Design 组合方式。
- 用户可见文案必须走现有 i18n 路径。
- 页面交互必须覆盖加载、空状态、成功、失败和禁用/进行中状态。
- Status、Config、DNS、Logs 页面必须保持窄屏可用，不得出现文本溢出、控件遮挡或布局跳动。

## 6. Agent 工作流

所有非琐碎工作都应按以下顺序推进：

1. 需求 Agent 定义用户目标、约束、验收标准和风险。
2. 设计 Agent 将已确认的需求转成架构、UI/API 契约、数据流和发布说明。
3. 后端 Agent 实现运行时、API、配置、路由、DNS、托盘或平台相关变化。
4. 前端 Agent 按已确认 API 和设计实现 Web 控制台变化。
5. 测试 Agent 验证行为、补充或调整测试、运行可行检查，并记录残余风险。

只有设计 Agent 明确后端/前端契约后，后端和前端才可以并行推进。

## 7. 完成定义

- 需求和设计假设已在任务记录、规格文档或 PR 摘要中可见。
- 用户可见行为已在正确项目边界内实现。
- 有意义的纯逻辑和回归点已被测试覆盖。
- 前端和后端相关项目均已完成必要编译，且编译通过。
- 可行的测试/检查命令已运行；无法运行时必须说明原因。
- 命令、配置结构、API 结构、发布步骤或用户可见工作流变化时，文档已同步更新。
