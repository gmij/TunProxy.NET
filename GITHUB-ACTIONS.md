# 使用 GitHub Actions 自动编译

## 步骤

### 1. 初始化 Git 仓库

```bash
cd TunProxy.NET
git init
git add .
git commit -m "Initial commit"
```

### 2. 创建 GitHub 仓库

在 GitHub 上创建新仓库（例如：`TunProxy.NET`），然后：

```bash
git remote add origin https://github.com/YOUR_USERNAME/TunProxy.NET.git
git branch -M main
git push -u origin main
```

### 3. 触发自动编译

推送后，GitHub Actions 会自动：
- ✅ 下载 wintun.dll
- ✅ 编译 .NET 8 项目
- ✅ 发布 AOT 版本（单文件，~15MB）
- ✅ 发布普通版本（带运行时，~60MB）
- ✅ 生成 ZIP 压缩包

### 4. 下载编译好的程序

**方式 A：从 Actions 下载**
1. 进入 GitHub 仓库 → Actions 标签
2. 点击最新的 build
3. 在底部下载 artifacts（ZIP 文件）

**方式 B：从 Release 下载（打 tag 时）**
```bash
# 本地打 tag
git tag v1.0.0
git push origin v1.0.0
```

推送 tag 后，Actions 会自动创建 GitHub Release，直接下载即可。

---

## 下载后的文件

### AOT 版本（推荐）
```
TunProxy.NET-win-x64-AOT.zip
├── TunProxy.CLI.exe    (~15-20MB，AOT 编译)
└── wintun.dll          (~200KB)
```

### 普通版本
```
TunProxy.NET-win-x64-regular.zip
├── TunProxy.CLI.exe
├── .NET runtime DLLs
└── wintun.dll
```

---

## 使用方法

1. 解压 ZIP 文件
2. **以管理员身份运行** PowerShell 或 CMD
3. 执行：
   ```powershell
   .\TunProxy.CLI.exe -p 127.0.0.1:7890 -t socks5
   ```

---

## 配置 TUN 接口（首次运行需要）

```powershell
# 设置 TUN 接口 IP
netsh interface ip set address "TunProxy" static 10.0.0.1 255.255.255.0

# 添加默认路由（可选，让所有流量走 TUN）
route add 0.0.0.0 mask 0.0.0.0 10.0.0.1
```

---

## 常见问题

**Q: Actions 编译失败？**
A: 检查 `.github/workflows/build.yml` 是否正确，查看 Actions 日志。

**Q: 运行时提示找不到 wintun.dll？**
A: 确保 wintun.dll 和 TunProxy.CLI.exe 在同一目录。

**Q: 提示需要管理员权限？**
A: 右键 → 以管理员身份运行。
