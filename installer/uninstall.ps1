# Project Guardian 卸载脚本（需要管理员权限；会触发 UAC）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File installer\uninstall.ps1              # 保留数据（数据库/日志）
#   powershell -ExecutionPolicy Bypass -File installer\uninstall.ps1 -RemoveData # 连同 C:\ProgramData\Guardian 一并删除
#requires -RunAsAdministrator

param(
    [switch]$RemoveData
)

$ErrorActionPreference = "Continue"
$serviceName = "GuardianService"
$installDir = Join-Path $env:ProgramFiles "Guardian"

Write-Host "== Guardian 卸载 =="

# 1. 停止并删除服务（服务停止时会自动执行恢复流程，释放全部保护）
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host "停止服务..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

# 2. 删除快捷方式
$desktop = [Environment]::GetFolderPath("Desktop")
$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
foreach ($p in @((Join-Path $desktop "Guardian.lnk"), (Join-Path $startMenu "Guardian.lnk"))) {
    if ($p -and (Test-Path $p)) { Remove-Item $p -Force }
}

# 3. 删除注册表
Remove-Item -Path "HKLM:\SOFTWARE\Guardian" -Recurse -Force -ErrorAction SilentlyContinue

# 一并清掉开机自启，否则卸载后每次登录都会尝试拉起不存在的程序。
Remove-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name "Guardian" -ErrorAction SilentlyContinue

# 4. 删除安装目录
if (Test-Path $installDir) {
    Remove-Item $installDir -Recurse -Force
    Write-Host "已删除安装目录：$installDir"
}

# 5. 可选：删除数据
if ($RemoveData) {
    $dataDir = Join-Path $env:ProgramData "Guardian"
    if (Test-Path $dataDir) {
        Remove-Item $dataDir -Recurse -Force
        Write-Host "已删除数据目录：$dataDir"
    }
} else {
    Write-Host "已保留数据目录 C:\ProgramData\Guardian（数据库与日志）。如需彻底清除，请使用 -RemoveData。"
}

Write-Host "卸载完成。"
