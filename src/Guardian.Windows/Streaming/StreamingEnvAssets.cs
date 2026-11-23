namespace Guardian.Windows.Streaming;

/// <summary>一个钉死的可下载产物：版本、地址、大小与期望 SHA256。</summary>
public sealed record PinnedAsset
{
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string Url { get; init; }
    public required string FileName { get; init; }
    public required long SizeBytes { get; init; }
    public required string Sha256 { get; init; }

    public string Sha256Short => Sha256.Length >= 8 ? Sha256[..8] : Sha256;
}

/// <summary>
/// 一键启用所需的官方产物。刻意钉死版本+哈希：winget 的 latest 会漂，
/// 而 Sunshine 新版本改过 dd_*/虚拟 HID 的行为，配置写法必须跟着实测版本走。
/// 两个哈希都是本机直连完整下载后本地计算得到的（Sunshine 的与 winget 清单公布值一致）。
/// 地址取自 GitHub API 的 browser_download_url，不是拼出来的。
/// </summary>
public static class StreamingEnvAssets
{
    /// <summary>Sunshine 安装包（MSI，33,710,080 B，SHA256 与 winget 清单一致）。</summary>
    public static readonly PinnedAsset SunshineInstaller = new()
    {
        Name = "Sunshine",
        Version = "2026.914.233613",
        FileName = "Sunshine-Windows-AMD64-installer.msi",
        Url = "https://github.com/SunshineStream/Sunshine/releases/download/v2026.914.233613/Sunshine-Windows-AMD64-installer.msi",
        SizeBytes = 33_710_080,
        Sha256 = "1d7fed8beecd5889dc7ff14cf9f42d6d38f37c3066c13c6c2a5f4e91847e0ccf",
    };

    /// <summary>
    /// VDD 驱动包（driver-only，132,118 B）。官方没有公布该包的哈希，以我们实测值 + 目录内文件签名双重把关。
    /// INF 只有 NTamd64 段，所以这个包只适用于 x64；ARM64 机器必须换 VirtualDisplayDriver-ARM64.Driver.Only.zip
    /// 并另行取证哈希，因此非 x64 一律拒绝联网安装（见 StreamingEnvInstaller）。
    /// </summary>
    public static readonly PinnedAsset VirtualDisplayDriver = new()
    {
        Name = "Virtual Display Driver",
        Version = "25.7.23",
        FileName = "VirtualDisplayDriver-x86.Driver.Only.zip",
        Url = "https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.7.23/VirtualDisplayDriver-x86.Driver.Only.zip",
        SizeBytes = 132_118,
        Sha256 = "e24210692b442b39af763536330ce78b423f19342b7a7792c26de3944e418b3a",
    };
}
