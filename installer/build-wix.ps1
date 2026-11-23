# 构建 Guardian MSI 安装包（WiX v4，x64）。
# 前置：已运行 scripts\publish.ps1（自包含发布时产物带子目录与运行时，脚本会一并打包）。
# 用法：powershell -ExecutionPolicy Bypass -File installer\build-wix.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$publishRoot = Join-Path $root "artifacts\publish"
$outputDir = Join-Path $root "artifacts\installer"
$toolsDir = Join-Path $root "tools"
$wix = Join-Path $toolsDir "wix.exe"
$version = "0.1.5"
$offlineRoot = Join-Path $root "artifacts\offline"

if (-not (Test-Path (Join-Path $publishRoot "app\Guardian.App.exe"))) {
    throw "缺少发布产物，请先运行 scripts\publish.ps1"
}

# 离线依赖（Sunshine MSI + VDD 驱动 zip）必须齐备，否则装到机器上"一键启用"仍要联网。
$offlineFiles = @()
if (Test-Path $offlineRoot) { $offlineFiles = @(Get-ChildItem $offlineRoot -File) }
if ($offlineFiles.Count -eq 0) {
    throw "缺少离线依赖：请把 Sunshine 安装包与 VDD 驱动包按钉死文件名放到 artifacts\offline"
}

# 1. 确保 wix v4 工具（v6+ 需商业 OSMF 条款，因此固定 v4）
if (-not (Test-Path $wix)) {
    $dotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
    if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
    Write-Host "安装 WiX v4 工具（本地 tools 目录）..."
    & $dotnet tool install wix --version 4.0.5 --tool-path $toolsDir
    if ($LASTEXITCODE -ne 0) { throw "wix 工具安装失败" }
}

# 2. 生成 wxs（按发布产物显式枚举文件）。
#    自包含发布带语言资源子目录（cs/、de/ 等）：子目录必须映射成 MSI 目录树，
#    否则同名资源文件会被压平到同一目标路径（WIX0369 重复 GUID 的根因）。
#    组件 GUID 按"发布根相对路径"确定性生成：跨构建稳定，升级不残留孤儿组件。
function Escape-Xml([string]$value) {
    return [System.Security.SecurityElement]::Escape($value)
}

function New-DeterministicGuid([string]$key) {
    $md5 = [System.Security.Cryptography.MD5]::Create()
    $hash = $md5.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($key))
    $bytes = New-Object byte[] 16
    [Array]::Copy($hash, $bytes, 16)
    $bytes[7] = ($bytes[7] -band 0x0F) -bor 0x50   # 版本位
    $bytes[8] = ($bytes[8] -band 0x3F) -bor 0x80   # 变体位
    return (New-Object System.Guid -ArgumentList (,$bytes)).ToString()
}

$appRoot = Join-Path $publishRoot "app"
$svcRoot = Join-Path $publishRoot "service"

function New-DirId([string]$baseId, [string]$fullPath, [string]$rootPath) {
    $rel = $fullPath.Substring($rootPath.Length).TrimStart('\').Replace('\', '_').Replace('.', '_').Replace('-', '_')
    return "$baseId`_$rel"
}

function Emit-DirectoryTree([string]$fullPath, [string]$id, [string]$rootPath, [string]$baseId, [int]$indent, [System.Text.StringBuilder]$target) {
    $pad = '  ' * $indent
    $name = Escape-Xml (Split-Path $fullPath -Leaf)
    [void]$target.AppendLine("${pad}<Directory Id=`"$id`" Name=`"$name`">")
    foreach ($sub in (Get-ChildItem $fullPath -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
        Emit-DirectoryTree $sub.FullName (New-DirId $baseId $sub.FullName $rootPath) $rootPath $baseId ($indent + 1) $target
    }
    [void]$target.AppendLine("${pad}</Directory>")
}

$appComponents = New-Object System.Text.StringBuilder
$svcComponents = New-Object System.Text.StringBuilder
$offlineComponents = New-Object System.Text.StringBuilder
$script:counter = 0

function Add-FileComponents([string]$rootPath, [string]$baseId, [System.Text.StringBuilder]$target, [string]$prefix, [string[]]$exclude, [string]$guidRoot) {
    foreach ($file in (Get-ChildItem $rootPath -Recurse -File | Where-Object { $exclude -notcontains $_.Name })) {
        $script:counter++
        $relDir = $file.DirectoryName.Substring($rootPath.Length).TrimStart('\')
        $dirId = $baseId
        if ($relDir) { $dirId = New-DirId $baseId $file.DirectoryName $rootPath }
        # 组件 GUID 按相对 guidRoot 的路径确定性生成：跨构建稳定，路径本身也保证全局唯一。
        $relPath = $file.FullName.Substring($guidRoot.Length).TrimStart('\')
        $guid = New-DeterministicGuid $relPath
        $escaped = Escape-Xml $file.FullName
        [void]$target.AppendLine('        <Component Id="' + $prefix + $script:counter + '" Guid="' + $guid + '" Directory="' + $dirId + '"><File Id="' + $prefix + $script:counter + '_f" Source="' + $escaped + '" /></Component>')
    }
}

$appDirs = New-Object System.Text.StringBuilder
foreach ($sub in (Get-ChildItem $appRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    Emit-DirectoryTree $sub.FullName (New-DirId "AppDir" $sub.FullName $appRoot) $appRoot "AppDir" 10 $appDirs
}
$svcDirs = New-Object System.Text.StringBuilder
foreach ($sub in (Get-ChildItem $svcRoot -Directory -ErrorAction SilentlyContinue | Sort-Object Name)) {
    Emit-DirectoryTree $sub.FullName (New-DirId "ServiceDir" $sub.FullName $svcRoot) $svcRoot "ServiceDir" 10 $svcDirs
}

Add-FileComponents $appRoot "AppDir" $appComponents "AppFile" @() $publishRoot
Add-FileComponents $svcRoot "ServiceDir" $svcComponents "SvcFile" @("Guardian.Service.exe") $publishRoot
Add-FileComponents $offlineRoot "OfflineDir" $offlineComponents "OfflineFile" @() $root

$serviceExe = Escape-Xml (Join-Path $publishRoot "service\Guardian.Service.exe")

$wxsHeader = @"
<!-- 由 build-wix.ps1 自动生成，勿手改。 -->
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="Project Guardian" Manufacturer="Guardian" Version="$version"
           UpgradeCode="8E7A2C4F-3B1D-4A5E-9C0F-2D6E7B8A1C42" Scope="perMachine"
           Language="2052" Compressed="yes">
    <MajorUpgrade DowngradeErrorMessage="已安装更高版本的 Guardian，请先卸载。" />
    <MediaTemplate EmbedCab="yes" />
    <StandardDirectory Id="ProgramFiles64Folder">
      <Directory Id="INSTALLFOLDER" Name="Guardian">
        <Directory Id="AppDir" Name="app">
"@

$wxsAfterApp = @"
        </Directory>
        <Directory Id="ServiceDir" Name="service">
"@

$wxsBody = @"
        </Directory>
        <Directory Id="OfflineDir" Name="offline" />
      </Directory>
    </StandardDirectory>
    <StandardDirectory Id="DesktopFolder" />
    <StandardDirectory Id="ProgramMenuFolder">
      <Directory Id="ApplicationProgramsFolder" Name="Guardian" />
    </StandardDirectory>

    <ComponentGroup Id="AppFiles" Directory="AppDir">
"@

$wxsFooter = @"
    </ComponentGroup>
    <ComponentGroup Id="ServiceFiles" Directory="ServiceDir">
"@

$wxsTail = @"
    </ComponentGroup>
    <ComponentGroup Id="OfflineFiles" Directory="OfflineDir">
"@

$wxsTail2 = @"
    </ComponentGroup>

    <Component Id="GuardianServiceComponent" Directory="ServiceDir" Guid="*">
      <File Id="GuardianServiceExe" Source="$serviceExe" KeyPath="yes" />
      <ServiceInstall Id="GuardianServiceInstall" Type="ownProcess" Vital="yes"
                      Name="GuardianService" DisplayName="Guardian 工作环境保护服务"
                      Description="管理 Windows 工作环境显示隐私、输入隔离、电源与恢复策略的本地后台服务。"
                      Start="auto" Account="LocalSystem" ErrorControl="normal" />
      <ServiceControl Id="GuardianServiceControl" Name="GuardianService"
                      Start="install" Stop="both" Remove="uninstall" Wait="yes" />
    </Component>

    <Component Id="RegistryComponent" Directory="INSTALLFOLDER" Guid="*">
      <RegistryKey Root="HKLM" Key="SOFTWARE\Guardian">
        <RegistryValue Name="InstallDir" Value="[INSTALLFOLDER]" Type="string" KeyPath="yes" />
        <RegistryValue Name="Version" Value="$version" Type="string" />
      </RegistryKey>
    </Component>

    <Component Id="AutostartComponent" Directory="INSTALLFOLDER" Guid="*">
      <RegistryValue Root="HKCU" Key="SOFTWARE\Microsoft\Windows\CurrentVersion\Run" Name="Guardian"
                     Value="&quot;[AppDir]Guardian.App.exe&quot; --minimized" Type="string" KeyPath="yes" />
    </Component>

    <Component Id="ShortcutsComponent" Directory="INSTALLFOLDER" Guid="*">
      <Shortcut Id="DesktopShortcut" Directory="DesktopFolder" Name="Guardian"
                Description="Guardian 工作环境保护" WorkingDirectory="AppDir"
                Target="[AppDir]Guardian.App.exe" />
      <Shortcut Id="StartMenuShortcut" Directory="ApplicationProgramsFolder" Name="Guardian"
                Description="Guardian 工作环境保护" WorkingDirectory="AppDir"
                Target="[AppDir]Guardian.App.exe" />
      <RemoveFolder Id="RemoveApplicationProgramsFolder" Directory="ApplicationProgramsFolder" On="uninstall" />
      <RegistryValue Root="HKCU" Key="SOFTWARE\Guardian" Name="Shortcuts" Value="1" Type="integer" KeyPath="yes" />
    </Component>

    <Feature Id="MainFeature" Title="Project Guardian" Level="1">
      <ComponentGroupRef Id="AppFiles" />
      <ComponentGroupRef Id="ServiceFiles" />
      <ComponentGroupRef Id="OfflineFiles" />
      <ComponentRef Id="GuardianServiceComponent" />
      <ComponentRef Id="RegistryComponent" />
      <ComponentRef Id="AutostartComponent" />
      <ComponentRef Id="ShortcutsComponent" />
    </Feature>
  </Package>
</Wix>
"@

$wxs = $wxsHeader + $appDirs.ToString() + $wxsAfterApp + $svcDirs.ToString() + $wxsBody + $appComponents.ToString() + $wxsFooter + $svcComponents.ToString() + $wxsTail + $offlineComponents.ToString() + $wxsTail2

$generatedWxs = Join-Path $outputDir "Guardian.generated.wxs"
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Set-Content -Path $generatedWxs -Value $wxs -Encoding UTF8

# 3. 构建（-arch x64：WiX v4 中包架构由构建参数决定，Package 元素没有 Platform 属性）
Write-Host "构建 MSI（x64）..."
& $wix build $generatedWxs -arch x64 -o (Join-Path $outputDir "Guardian-$version.msi")
if ($LASTEXITCODE -ne 0) { throw "MSI 构建失败" }

Write-Host "MSI 已生成：$(Join-Path $outputDir "Guardian-$version.msi")"
