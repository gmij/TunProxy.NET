# TunProxy.NET 主 Agent 流程

本文件是 Claude、Codex、GitHub Copilot 或人工协调者共用的主流程。各框架入口只负责加载本文件和角色文件，不能各自维护一套不同规则。

## 流程

1. 从需求 Agent 开始。
   - 明确问题、用户、运行模式、平台和验收标准。
   - 如果请求与项目宪章冲突，应先停下来说明冲突。
2. 进入设计 Agent。
   - 明确后端、前端、API、配置和测试边界。
   - 实现开始前必须先确定交接契约。
3. 分派后端 Agent 和/或前端 Agent。
   - 后端负责 .NET 运行时、API、CLI、托盘、路由、DNS、资源和平台行为。
   - 前端负责静态 Web 控制台页面、共享 shell、API 调用、i18n 和响应式 UI。
   - 只有设计已冻结 API 和数据契约后，前后端才可以并行。
4. 最后进入测试 Agent。
   - 补充或调整聚焦测试。
   - 运行可行检查。
   - 汇总已验证行为和残余风险。

## 默认任务产物

较大的工作应在 `docs/specs/<feature-name>/` 下保留记录：

- `requirements.md`
- `design.md`
- `implementation.md`
- `test-report.md`

小修复可以把这些内容放在 PR 或最终回复中，不一定新增文件。

## 路由规则

- 只改文案或文档时，可以只使用需求 Agent 和测试 Agent。
- 涉及 API 响应结构、配置结构、路由行为、DNS 行为、TUN 行为或托盘生命周期时，必须经过需求、设计、后端和测试。
- 涉及 `src/TunProxy.CLI/wwwroot` 时，必须包含前端和测试。
- 前后端对契约理解不一致时，由设计 Agent 先修正契约，再继续实现。

## 编译要求

- 无论修改前端还是后端，只要涉及可编译项目，完成前都必须编译通过。
- 后端、API、CLI、DNS、路由、资源或 Web 控制台修改必须运行：

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
```

- Windows 托盘修改还必须运行：

```powershell
dotnet build src\TunProxy.Tray\TunProxy.Tray.csproj -v minimal
```

- 无法编译时，任务不得标记完成，必须报告阻塞原因和失败信息。

## 共享护栏

- GeoIP 或 GFWList 资源缺失/无效时，必须保留本地代理设置模式。
- 单元测试不得依赖真实 TUN 设备、管理员权限、服务安装或实时网络。
- Web 控制台必须保持当前静态 Vue global + Ant Design 架构，除非需求明确批准迁移。
- 前端实现必须严格遵守 Vue + Ant Design 最佳实践，复用现有组件、API 客户端、i18n 和样式模式。
- 避免无关重构。
- 测试和文档投入应与变更风险匹配。
