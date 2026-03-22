# AOT 发布指南

## 前置条件

1. 安装 .NET 8 SDK
2. 安装 Visual Studio 2022 或 VS Build Tools
3. 安装 C++ 桌面开发工作负载（AOT 需要 MSVC）

## 发布步骤

### 1. 下载 wintun.dll

```powershell
.\download-wintun.ps1
```

或手动从 https://www.wintun.net/ 下载。

### 2. 发布 AOT 版本

```bash
dotnet publish src/TunProxy.CLI/TunProxy.CLI.csproj `
  -c Release `
  -r win-x64 `
  /p:PublishAot=true `
  /p:IlcOptimizationPreference=Size `
  /p:StripSymbols=true
```

### 3. 输出文件

发布后，可执行文件位于：
```
src/TunProxy.CLI/bin/Release/net8.0/win-x64/publish/TunProxy.CLI.exe
```

同时需要把 `wintun.dll` 一起分发。

### 4. 文件大小参考

- TunProxy.CLI.exe (AOT): ~15-20 MB
- wintun.dll: ~200 KB

## 优化选项

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <IlcOptimizationPreference>Size</IlcOptimizationPreference>
  <StripSymbols>true</StripSymbols>
  <InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup>
```

- `IlcOptimizationPreference`: Size（体积小）或 Performance（性能好）
- `StripSymbols`: 移除调试符号，减小体积
- `InvariantGlobalization`: 移除全球化数据，减小体积（但会失去本地化支持）

## 常见问题

### Q: 发布时报错 "C++ 编译器未找到"

A: 安装 Visual Studio 2022 并选择 "使用 C++ 的桌面开发" 工作负载。

### Q: 运行时提示 "找不到 wintun.dll"

A: 确保 wintun.dll 和 TunProxy.CLI.exe 在同一目录。

### Q: 启动时提示 "需要管理员权限"

A: 右键 → 以管理员身份运行，或使用 `Start-Process -Verb RunAs`。

## 一键发布脚本

```powershell
# publish.ps1
$ErrorActionPreference = "Stop"

Write-Host "发布 AOT 版本..."

dotnet publish src/TunProxy.CLI/TunProxy.CLI.csproj `
  -c Release `
  -r win-x64 `
  /p:PublishAot=true `
  /p:IlcOptimizationPreference=Size

Write-Host "复制 wintun.dll..."
Copy-Item wintun.dll src/TunProxy.CLI/bin/Release/net8.0/win-x64/publish/

Write-Host "✓ 完成！"
Write-Host "输出目录：src/TunProxy.CLI/bin/Release/net8.0/win-x64/publish/"
```
