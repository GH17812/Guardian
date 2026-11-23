using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace Guardian.Windows.Streaming;

/// <summary>静默安装结果，Evidence 只写看得见的东西（退出码、后续实测状态）。</summary>
public sealed record InstallOutcome(bool Ok, string Evidence);

/// <summary>
/// 静默安装 Sunshine 与 VDD 驱动包。全部动作都以 SYSTEM 身份在会话 0 内完成，不弹 UAC、不弹界面。
/// 命令行输出一个字都不解析：pnputil/MSI 的中文输出是 OEM 代码页，在 .NET 里读回是乱码，
/// 拿它当依据会把“成功”读成“失败”；结论一律改用退出码 + 安装后重新探测的实测状态。
/// </summary>
public static class StreamingEnvInstaller
{
    /// <summary>钉死的 VDD driver-only 包只有 NTamd64 段，非 x64 必须走别的路径（另行取证哈希）。</summary>
    public static bool SupportsCurrentArchitecture() => Environment.Is64BitOperatingSystem && !IsArm64();

    public static async Task<InstallOutcome> InstallSunshineAsync(string msiPath, CancellationToken token)
    {
        if (!File.Exists(msiPath))
        {
            return new InstallOutcome(false, $"安装包不存在：{msiPath}");
        }

        var logPath = Path.Combine(StreamingEnvCache.Directory, "sunshine-msi.log");
        var arguments = $"/i \"{msiPath}\" /qn /norestart /l*v \"{logPath}\"";
        var exit = await RunAsync("msiexec.exe", arguments, TimeSpan.FromMinutes(15), token).ConfigureAwait(false);

        var (ok, note) = exit switch
        {
            0 => (true, "安装完成"),
            3010 => (true, "安装完成，但系统提示需要重启才能全部生效"),
            1602 => (false, "安装被取消"),
            1603 => (false, "安装失败（致命错误，详见 msiexec 日志）"),
            1618 => (false, "已有另一个安装在进行，请稍后重试"),
            _ => (false, $"安装返回码 {exit}"),
        };

        return new InstallOutcome(ok, $"msiexec {note}；日志 {logPath}。");
    }

    /// <summary>解开 driver-only 包，返回可交给 pnputil 的 INF 路径。</summary>
    public static InstallOutcome ExtractDriver(string zipPath, out string infPath)
    {
        infPath = string.Empty;
        if (!File.Exists(zipPath))
        {
            return new InstallOutcome(false, $"驱动包不存在：{zipPath}");
        }

        var target = Path.Combine(StreamingEnvCache.Directory, "vdd");
        try
        {
            // 每次解到干净目录，避免上一次留下的旧 INF 被当成当前驱动包。
            if (System.IO.Directory.Exists(target))
            {
                System.IO.Directory.Delete(target, recursive: true);
            }

            ZipFile.ExtractToDirectory(zipPath, target);
        }
        catch (Exception ex)
        {
            return new InstallOutcome(false, $"解压驱动包失败：{ex.Message}");
        }

        var inf = Directory.EnumerateFiles(target, "*.inf", SearchOption.AllDirectories)
            .FirstOrDefault(candidate => Path.GetFileName(candidate).StartsWith("mttvdd", StringComparison.OrdinalIgnoreCase));

        if (inf is null)
        {
            return new InstallOutcome(false, $"解压后没找到 MttVDD INF：{target}");
        }

        infPath = inf;
        return new InstallOutcome(true, $"已解出驱动文件，INF = {inf}。");
    }

    /// <summary>
    /// 放置驱动的建屏设置。全新机实测：MttVDD 只在设备启动时读
    /// <c>C:\VirtualDisplayDriverdd_settings.xml</c>，没有这个文件就一个监视器都不创建
    /// （厂商工具那句 "No sample XML file found to create default settings" 是同一个原因）。
    /// 只补缺、绝不覆盖：用户或工具改过的配置必须原样留着。
    /// </summary>
    public static InstallOutcome TrySeedDriverSettings(string extractedDirectory, out string settingsPath)
    {
        settingsPath = Path.Combine(@"C:\VirtualDisplayDriver", "vdd_settings.xml");

        var source = Directory.EnumerateFiles(extractedDirectory, "vdd_settings.xml", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (source is null)
        {
            return new InstallOutcome(false, $"驱动包里没有 vdd_settings.xml：{extractedDirectory}");
        }

        if (File.Exists(settingsPath))
        {
            return new InstallOutcome(true, $"驱动设置已存在，未改动：{settingsPath}");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.Copy(source, settingsPath);
            return new InstallOutcome(true, $"已写入驱动建屏设置（来自钉死包，未覆盖已有文件）：{settingsPath}");
        }
        catch (Exception ex)
        {
            return new InstallOutcome(false, $"写入 {settingsPath} 失败：{ex.Message}");
        }
    }

    /// <summary>把驱动包加入驱动存储并尝试装到已存在的设备。装完由调用方重新探测设备，不靠命令行回显。</summary>
    public static async Task<InstallOutcome> AddDriverAsync(string infPath, CancellationToken token)
    {
        if (!File.Exists(infPath))
        {
            return new InstallOutcome(false, $"INF 不存在：{infPath}");
        }

        var exit = await RunAsync("pnputil.exe", $"/add-driver \"{infPath}\" /install", TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
        return exit switch
        {
            0 => new InstallOutcome(true, "pnputil 已把驱动包加入驱动存储（退出码 0）。"),
            3010 => new InstallOutcome(true, "pnputil 已加入驱动包，但提示需要重启（退出码 3010）。"),
            // 失败码统一交给 ExplainAddDriverFailure 翻译：只映射真机实测遇到过的，其余如实给码与日志位置。
            _ => new InstallOutcome(false, ExplainAddDriverFailure(exit, infPath)),
        };
    }

    /// <summary>
    /// 把 pnputil 的失败码翻译成能照做的下一步。官方文档只列了 0/259/3010/1641，
    /// 其余码我不编故事：给十六进制 + 指向系统自己的日志。
    /// </summary>
    public static string ExplainAddDriverFailure(int exitCode, string infPath)
    {
        var where = "驱动包路径 " + infPath
            + "；细节见 %windir%\\inf\\setupapi.dev.log（add-driver 记在 dev.log，不是 app.log）。";

        return exitCode switch
        {
            // 0xE0000242：新机实测。文件签名有效，但发布者尚未受信；
            // 服务在非交互会话里弹不出"是否信任该发布者"的确认框，Windows 直接按"用户不想安装"处理。
            // 常见成因就是那台机器没有可用网络，自动根证书更新取不到厂商根证书。
            unchecked((int)0xE0000242) =>
                "驱动签名有效，但发布者尚未受信（0xE0000242）。"
                + "通常是这台机器无法联网更新受信任根证书，而 Guardian 服务在非交互会话中弹不出信任确认框，"
                + "Windows 便按「用户拒绝安装」处理。任选一条后再点一次一键启用："
                + "① 在管理员 PowerShell 里手工执行 pnputil /add-driver 并信任该发布者；"
                + "② 临时联网一次（手机热点即可，不需要能访问 GitHub）；"
                + "③ 手动导入该厂商的根证书到「受信任的根证书颁发机构」。",

            _ => $"pnputil 返回码 {exitCode}（0x{unchecked((uint)exitCode):X8}）。{where}",
        };
    }

    private static async Task<int> RunAsync(string fileName, string arguments, TimeSpan timeout, CancellationToken token)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {fileName}");

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeoutSource.CancelAfter(timeout);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // 绝不强杀 msiexec/pnputil：半途杀死安装比让它跑完更危险。
            if (process.HasExited)
            {
                return process.ExitCode;
            }

            throw new InvalidOperationException($"{fileName} 仍在运行（可能正在写系统状态），本次等待已取消，请等它结束后再试。");
        }
    }

    private static bool IsArm64()
        => RuntimeInformation.OSArchitecture == Architecture.Arm64;
}
