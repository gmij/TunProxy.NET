# 🚀 快速部署到 GitHub

## 方式 A: 手动创建（推荐）

### 1. 在 GitHub 创建仓库

访问 https://github.com/new

- **Repository name:** `TunProxy.NET`
- **Description:** `.NET 8 TUN Proxy with Native AOT support`
- **Visibility:** Public（开源）或 Private（私有）
- **不要** 初始化 README（我们已经有了）

### 2. 添加远程仓库并推送

```bash
cd /root/.openclaw/workspace/TunProxy.NET

# 替换 YOUR_USERNAME 为你的 GitHub 用户名
git remote add origin https://github.com/YOUR_USERNAME/TunProxy.NET.git

# 重命名分支为 main
git branch -M main

# 推送
git push -u origin main
```

### 3. 等待 GitHub Actions 编译

推送后，GitHub 会自动：
1. 运行单元测试
2. 编译 AOT 版本
3. 生成 ZIP 文件

查看进度：https://github.com/YOUR_USERNAME/TunProxy.NET/actions

### 4. 下载编译好的程序

**从 Actions 下载：**
1. 进入 Actions 标签
2. 点击最新的 "Build and Release"
3. 滚动到底部，下载 `TunProxy.NET-win-x64-AOT.zip`

**从 Release 下载（打 tag 后）：**
```bash
# 打 tag 并推送
git tag v1.0.0
git push origin v1.0.0
```

然后进入 Releases 标签下载。

---

## 方式 B: 使用 GitHub CLI（如果你已经安装了 gh）

```bash
# 创建仓库
gh repo create TunProxy.NET --public --source=. --remote=origin --push

# 或者私有仓库
gh repo create TunProxy.NET --private --source=. --remote=origin --push
```

---

## 推送后会发生什么？

### GitHub Actions 流程

```
push → Actions 触发
     ↓
1. 下载 wintun.dll
     ↓
2. dotnet restore
     ↓
3. dotnet build
     ↓
4. dotnet test (单元测试)
     ↓
5. dotnet publish (AOT)
     ↓
6. 打包 ZIP
     ↓
7. 上传到 Artifacts / Release
```

### 测试通过标准

- ✅ 所有单元测试通过
- ✅ AOT 编译成功
- ✅ ZIP 文件生成

如果任何一步失败，Actions 会标记为失败，查看日志修复。

---

## 下载后怎么用？

1. 解压 `TunProxy.NET-win-x64-AOT.zip`
2. 以**管理员身份**运行 PowerShell
3. 执行：
   ```powershell
   .\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5
   ```

---

## 需要帮助？

- 查看 [README.md](README.md) 了解项目
- 查看 [TESTING.md](TESTING.md) 了解测试
- 查看 [GITHUB-ACTIONS.md](GITHUB-ACTIONS.md) 了解 CI/CD
- 查看 [AOT-PUBLISH.md](AOT-PUBLISH.md) 了解 AOT 发布

---

_斌哥，推送到 GitHub 后，Actions 会自动编译好，你直接下载就能用！_ 🥔
