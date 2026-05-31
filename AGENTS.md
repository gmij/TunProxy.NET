# TunProxy.NET Agent 指令

本仓库对 Codex、Claude、GitHub Copilot 使用同一套共享 agent 定义。

## 阅读顺序

1. `docs/constitution.md`
2. `docs/agents/main-flow.md`
3. 与当前任务匹配的角色文件：
   - `docs/agents/requirements-agent.md`
   - `docs/agents/design-agent.md`
   - `docs/agents/backend-agent.md`
   - `docs/agents/frontend-agent.md`
   - `docs/agents/test-agent.md`

## 必须遵守的流程

非琐碎工作按以下顺序推进：

需求 -> 设计 -> 后端和/或前端 -> 测试

只有设计 Agent 已经明确 API、配置和 UI 契约后，后端和前端才可以并行。

## 仓库护栏

- GeoIP 或 GFWList 资源缺失/无效时，必须保留本地代理设置模式。
- 平台相关行为放在边界层，纯决策逻辑必须可测试。
- 单元测试不得依赖真实 TUN 设备、管理员权限、已安装 Windows 服务或实时网络。
- Web 控制台保持当前静态 Vue global + Ant Design 架构，除非需求明确批准引入构建系统。
- 无论修改前端还是后端，完成前都必须运行相关项目编译，编译通过才算完成。
- 前端必须严格遵守 Vue + Ant Design 最佳实践。
