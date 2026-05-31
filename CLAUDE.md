# TunProxy.NET Claude 指令

使用本仓库的共享 agent 定义。除非共享定义本身需要更新，否则不要创建 Claude 专属的另一套角色规则。

## 阅读顺序

1. `docs/constitution.md`
2. `docs/agents/main-flow.md`
3. 相关共享角色文件：
   - `docs/agents/requirements-agent.md`
   - `docs/agents/design-agent.md`
   - `docs/agents/backend-agent.md`
   - `docs/agents/frontend-agent.md`
   - `docs/agents/test-agent.md`

## 工作流

非琐碎工作按以下顺序推进：

需求 -> 设计 -> 后端和/或前端 -> 测试

`.claude/agents/` 只作为 Claude 启动入口。权威角色定义位于 `docs/agents/`。
