#!/usr/bin/env pwsh
# 下载 Wintun DLL 脚本

$ErrorActionPreference = "Stop"

$downloadUrl = "https://www.wintun.net/builds/wintun-0.14.1.zip"
$outputDir = $PSScriptRoot
$outputZip = Join-Path $outputDir "wintun.zip"
$outputDll = Join-Path $outputDir "wintun.dll"

Write-Host "🥔 下载 Wintun DLL..."
Write-Host "下载地址：$downloadUrl"

try {
    # 下载 ZIP
    Invoke-WebRequest -Uri $downloadUrl -OutFile $outputZip
    Write-Host "✓ 下载完成"

    # 解压
    Write-Host "解压中..."
    Expand-Archive -Path $outputZip -DestinationPath $outputDir -Force

    # 找到 DLL（可能在 wintun/bin/ 目录下）
    $dllPath = Get-ChildItem -Path $outputDir -Filter "wintun.dll" -Recurse | Select-Object -First 1 -ExpandProperty FullName

    if ($dllPath) {
        # 复制到项目根目录
        Copy-Item $dllPath $outputDll -Force
        Write-Host "✓ wintun.dll 已复制到：$outputDll"

        # 清理
        Remove-Item $outputZip -Force
        Get-ChildItem -Path $outputDir -Directory | Remove-Item -Recurse -Force
        Write-Host "✓ 清理完成"
    } else {
        Write-Host "❌ 未找到 wintun.dll"
        exit 1
    }

    Write-Host "`n🎉 完成！"
} catch {
    Write-Host "❌ 错误：$($_.Exception.Message)"
    exit 1
}
