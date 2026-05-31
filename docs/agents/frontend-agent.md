# 前端 Agent

## 使命

基于现有嵌入式静态 Vue + Ant Design 架构，实现 TunProxy.NET Web 控制台变化。

## 优先阅读

- `docs/constitution.md`
- 设计 Agent 输出
- `src/TunProxy.CLI/wwwroot/api.js`
- `src/TunProxy.CLI/wwwroot/i18n.js`
- `src/TunProxy.CLI/wwwroot/nav.js`
- `src/TunProxy.CLI/wwwroot/console-app.js`
- `src/TunProxy.CLI/wwwroot/console.css`
- 相关页面文件：`status-page.js`、`config-page.js`、`dns-page.js` 或 `logs-page.js`

## 所有权

- 静态页面：`index.html`、`config.html`、`dns.html`、`logs.html`
- 页面逻辑：`status-page.js`、`config-page.js`、`dns-page.js`、`logs-page.js`
- 共享 shell 和组件：`console-app.js`、`nav.js`、`console.css`
- API 客户端：`api.js`
- 本地化：`i18n.js`

## 实现规则

- 不得引入 Node 构建流水线，除非批准后的设计明确要求。
- 必须严格遵守 Vue + Ant Design 最佳实践。
- 复用 Ant Design 组件和现有共享 shell；不要绕开既有模式自造控件。
- 状态应有清晰来源；表单、表格、筛选、按钮、弹窗、消息反馈应使用语义正确的 Ant Design 组件。
- API 调用必须处理加载、空状态、成功、失败和进行中禁用状态。
- Web 控制台是运维型界面，应保持可扫描、可重复操作、信息密度适中。
- 用户可见文案必须走现有 i18n 路径。
- 保持移动端布局，不得出现文本溢出、控件遮挡或布局跳动。
- 不得虚构 API 字段。使用设计 Agent 契约，或先与后端 Agent 对齐。

## 编译和验证

前端修改完成前必须运行嵌入资源所在项目的编译，编译通过才算完成：

```powershell
dotnet build src\TunProxy.CLI\TunProxy.CLI.csproj -v minimal
```

根据变化范围补充：

```powershell
dotnet test tests\TunProxy.Tests\TunProxy.Tests.csproj -v minimal
git diff --check
```

- 优先使用现有 `WebConsoleAssetTests` 检查嵌入资源。
- 改布局或交互时，应手工检查页面，尤其是窄屏表现。

## 交接

报告以下内容：

- 修改的页面和共享组件
- 消费的 API 字段和动作
- 新增或修改的 i18n key
- 已完成的视觉/响应式检查
- 后端或 API 假设
- 编译命令和结果
