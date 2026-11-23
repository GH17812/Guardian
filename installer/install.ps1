# Project Guardian 安装脚本（需要管理员权限；会触发 UAC）。
# 功能：复制发布产物到 Program Files、注册 Windows 服务、写入注册表配置、创建快捷方式、启动服务。
# 前置：先运行 scripts\publish.ps1（或加 -SkipPublish 使用已有 artifacts\publish）。
# 用法：
#   powershell -ExecutionPolicy Bypass -File installer\install.ps1
#   powershell -ExecutionPolicy Bypass -File installer\install.ps1 -SkipPublish
#requires -RunAsAdministrator

param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $root "artifacts\publish"
$installDir = Join-Path $env:ProgramFiles "Guardian"
$serviceName = "GuardianService"

function Test-DotNet10Runtime {
    $dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    try {
        $runtimes = & $dotnet --list-runtimes 2>$null
        return [bool]($runtimes | Select-String "Microsoft.WindowsDesktop.App 10\.")
    } catch {
        return $false
    }
}

Write-Host "== Guardian 安装 =="

# 1. 发布
if (-not $SkipPublish) {
    if (-not (Test-Path (Join-Path $publishRoot "app\Guardian.App.exe"))) {
        Write-Host "未发现发布产物，开始发布..."
        & (Join-Path $root "scripts\publish.ps1")
    }
}

if (-not (Test-Path (Join-Path $publishRoot "app\Guardian.App.exe"))) {
    throw "发布产物缺失：请先运行 scripts\publish.ps1"
}

if (-not (Test-Path (Join-Path $publishRoot "service\Guardian.Service.exe"))) {
    throw "服务发布产物缺失：请先运行 scripts\publish.ps1"
}

# 2. 运行时检查（框架依赖发布）
if (-not (Test-Path (Join-Path $publishRoot "service\coreclr.dll"))) {
    if (-not (Test-DotNet10Runtime)) {
        throw "目标机器未安装 .NET 10 桌面运行时。请安装 Microsoft.DotNet.DesktopRuntime.10，或使用 -SelfContained 重新发布。"
    }
}

# 3. 停止并删除旧服务、关闭正在运行的客户端（都必须先于目录覆盖：运行中的 EXE/DLL 映像被系统占用，
#    下面的 Remove-Item 会失败并中断安装，实测表现为服务已被删除、安装目录仍是旧版——
#    客户端 Guardian.App 由交互会话用户启动，服务停不掉它，必须单独关闭）。
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "停止并删除旧服务..."
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 2
}

foreach ($procName in @("Guardian.App", "Guardian.Service")) {
    foreach ($p in @(Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        Write-Host "关闭运行中的 $procName (PID=$($p.Id))..."
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
    }
}
Start-Sleep -Milliseconds 800

# 4. 复制文件
if (Test-Path $installDir) {
    Write-Host "覆盖已存在的安装目录..."
    Remove-Item $installDir -Recurse -Force
}
# 必须先创建目标子目录：Copy-Item 通配符源 + 不存在目标目录时会把文件复制成同名文件
New-Item -ItemType Directory -Path (Join-Path $installDir "app") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $installDir "service") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $installDir "scripts") -Force | Out-Null
Copy-Item (Join-Path $publishRoot "app\*") (Join-Path $installDir "app\") -Recurse -Force
Copy-Item (Join-Path $publishRoot "service\*") (Join-Path $installDir "service\") -Recurse -Force
Copy-Item $PSCommandPath (Join-Path $installDir "scripts\install.ps1") -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") (Join-Path $installDir "scripts\uninstall.ps1") -Force

# 覆盖后校验：Copy-Item 保留源文件时间戳，若安装目录里的 exe 比发布产物旧，说明该文件根本没被替换
# （历史上正是这一步静默失败，装了个“旧版 + 无服务”的半成品状态）。
foreach ($check in @("app\Guardian.App.exe", "service\Guardian.Service.exe")) {
    $src = Get-Item (Join-Path $publishRoot $check)
    $dst = Get-Item (Join-Path $installDir $check)
    if ($dst.LastWriteTime -lt $src.LastWriteTime) {
        throw "安装未生效：$check 仍是旧版本（安装目录中的文件被占用），请关闭 Guardian 客户端后重试"
    }
}

# 5. 注册表
New-Item -Path "HKLM:\SOFTWARE\Guardian" -Force | Out-Null
Set-ItemProperty -Path "HKLM:\SOFTWARE\Guardian" -Name "InstallDir" -Value $installDir
Set-ItemProperty -Path "HKLM:\SOFTWARE\Guardian" -Name "Version" -Value "0.1.0"

# 6. 注册并启动服务
Write-Host "注册服务 $serviceName ..."
$binPath = "`"$(Join-Path $installDir 'service\Guardian.Service.exe')`""
$createOut = & sc.exe create $serviceName binPath= $binPath start= auto DisplayName= "Guardian 工作环境保护服务" 2>&1
$createOut | ForEach-Object { Write-Host "  sc: $_" }
if ($LASTEXITCODE -ne 0) { throw "服务创建失败" }

# 服务崩溃自动重启（fail-open 兜底：服务启动时执行恢复）
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "启动服务 $serviceName ..."
$startOut = & sc.exe start $serviceName 2>&1
$startOut | ForEach-Object { Write-Host "  sc: $_" }
if ($LASTEXITCODE -ne 0) { throw "服务启动失败" }

# 7. 开机自启（客户端必须常驻交互会话）
# 紧急恢复热键与显示隐私执行体只能在用户会话里生效；不注册自启时，重启后这两项其实都不存在，
# 而服务端仍会报"健康"——那是 fail-open 承诺上的漏洞。--minimized 让它开机在场但不弹窗口。
$runKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $runKey -Name "Guardian" -Value "`"$(Join-Path $installDir 'app\Guardian.App.exe')`" --minimized"
Write-Host "已注册开机自启：Guardian.App --minimized"

# 8. 快捷方式
$desktop = [Environment]::GetFolderPath("Desktop")
$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @($desktop, $startMenu)) {
    if (-not $dir) { continue }
    $lnk = $shell.CreateShortcut((Join-Path $dir "Guardian.lnk"))
    $lnk.TargetPath = Join-Path $installDir "app\Guardian.App.exe"
    $lnk.WorkingDirectory = Join-Path $installDir "app"
    $lnk.Description = "Guardian 工作环境保护"
    $lnk.Save()
}

Write-Host ""
Write-Host "安装完成。"
Write-Host "  安装目录：$installDir"
Write-Host "  数据目录：C:\ProgramData\Guardian（数据库与日志）"
Write-Host "  桌面/开始菜单已创建 Guardian 快捷方式。"
Write-Host "  紧急恢复快捷键：Ctrl+Alt+Shift+F12（输入隔离期间由服务端直接生效）"
