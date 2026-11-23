# Guardian 发布脚本：编译并发布 App 与 Service。
# 用法：
#   .\scripts\publish.ps1                  # 框架依赖发布（默认，体积小，需要目标机安装 .NET 10）
#   .\scripts\publish.ps1 -SelfContained   # 自包含发布（体积大，目标机无需 .NET 运行时）
param(
    [switch]$SelfContained,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

$dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
}

$outputRoot = Join-Path $root "artifacts\publish"
if (Test-Path $outputRoot) {
    Remove-Item $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

Write-Host "== 发布 Guardian.Service =="
& $dotnet publish (Join-Path $root "src\Guardian.Service\Guardian.Service.csproj") `
    -c $Configuration -r $Runtime --self-contained $SelfContained `
    -o (Join-Path $outputRoot "service")
if ($LASTEXITCODE -ne 0) { throw "Service 发布失败" }

Write-Host "== 发布 Guardian.App =="
& $dotnet publish (Join-Path $root "src\Guardian.App\Guardian.App.csproj") `
    -c $Configuration -r $Runtime --self-contained $SelfContained `
    -o (Join-Path $outputRoot "app")
if ($LASTEXITCODE -ne 0) { throw "App 发布失败" }

Write-Host "发布完成：$outputRoot"
