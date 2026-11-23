# Guardian 构建脚本：还原并编译整个解决方案（可选运行测试）。
# 用法：
#   .\scripts\build.ps1           # 仅构建
#   .\scripts\build.ps1 -Test     # 构建并运行全部测试
param(
    [switch]$Test,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

Write-Host "== 构建解决方案 =="
& $dotnet build (Join-Path $root "Guardian.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "构建失败" }

if ($Test) {
    Write-Host "== 运行测试 =="
    & $dotnet test (Join-Path $root "Guardian.slnx") -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "测试失败" }
}

Write-Host "完成。"
