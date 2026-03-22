@echo off
chcp 65001 >nul
echo 🥔 TunProxy.NET 构建脚本
echo.

echo 检查 .NET SDK...
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ❌ 未找到 .NET SDK，请先安装 .NET 8
    pause
    exit /b 1
)

echo ✓ .NET SDK 已安装
echo.

echo 检查 wintun.dll...
if not exist wintun.dll (
    echo ❌ 未找到 wintun.dll
    echo 请运行 download-wintun.ps1 下载
    echo 或从 https://www.wintun.net/ 手动下载
    pause
    exit /b 1
)
echo ✓ wintun.dll 已就绪
echo.

echo 构建项目...
dotnet build -c Release
if %errorlevel% neq 0 (
    echo ❌ 构建失败
    pause
    exit /b 1
)
echo ✓ 构建成功
echo.

echo 输出目录：src\TunProxy.CLI\bin\Release\net8.0\
echo.
echo 运行（需要管理员权限）:
echo   dotnet run --project src\TunProxy.CLI\TunProxy.CLI.csproj -c Release
echo.
echo 或发布 AOT 版本:
echo   dotnet publish src\TunProxy.CLI\TunProxy.CLI.csproj -c Release -r win-x64 /p:PublishAot=true
echo.

pause
