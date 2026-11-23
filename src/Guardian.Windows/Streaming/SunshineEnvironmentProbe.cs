using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Guardian.Windows.Streaming;

/// <summary>Sunshine 环境的只读快照，字段全部来自实测存在的来源（服务 ImagePath、安装目录、配置文件、服务状态）。</summary>
public sealed record SunshineEnvironmentSnapshot
{
    public required string InstallDirectory { get; init; }
    public required string ExecutablePath { get; init; }
    public required string ConfigDirectory { get; init; }
    public required string ConfigFilePath { get; init; }
    public required string LogFilePath { get; init; }
    public string Version { get; init; } = string.Empty;
    public bool ExecutableExists { get; init; }
    public bool ConfigFileExists { get; init; }
    public bool ServiceExists { get; init; }
    public bool ServiceRunning { get; init; }
    public bool ServiceAutoStart { get; init; }
    public string How { get; init; } = string.Empty;

    public bool Found => ExecutableExists && !string.IsNullOrEmpty(ConfigDirectory);
}

/// <summary>
/// Sunshine 环境只读探测。刻意不碰显示枚举（Session 0 枚举不到显示器），
/// 安装位置优先取服务 ImagePath：本机实测 <c>C:\Program Files\Sunshine\tools\sunshinesvc.exe</c>，
/// 卸载注册表的 InstallLocation 是空值，不能用。
/// </summary>
public static class SunshineEnvironmentProbe
{
    public const string ServiceName = "SunshineService";

    private static readonly string[] ConfigDirCandidateRelPaths =
    [
        Path.Combine("config"),
        Path.Combine("tools", "config"),
    ];

    public static SunshineEnvironmentSnapshot Read()
    {
        var (installDir, how) = ResolveInstallDirectory();
        var exe = Path.Combine(installDir, "sunshine.exe");
        var exeExists = File.Exists(exe);

        var (configDir, configSource) = ResolveConfigDirectory(installDir);
        var conf = Path.Combine(configDir, "sunshine.conf");

        return new SunshineEnvironmentSnapshot
        {
            InstallDirectory = installDir,
            ExecutablePath = exe,
            ConfigDirectory = configDir,
            ConfigFilePath = conf,
            LogFilePath = Path.Combine(configDir, "sunshine.log"),
            Version = exeExists ? ReadVersion(exe) : string.Empty,
            ExecutableExists = exeExists,
            ConfigFileExists = File.Exists(conf),
            ServiceExists = ReadServiceState(out var running, out var auto),
            ServiceRunning = running,
            ServiceAutoStart = auto,
            How = $"{how}{(string.IsNullOrEmpty(configSource) ? string.Empty : $"；配置目录来自 {configSource}")}",
        };
    }

    /// <summary>服务是否存在（不要求能读状态）。运行状态与自启动由 out 返回，读不到即为 false。</summary>
    private static bool ReadServiceState(out bool running, out bool auto)
    {
        running = false;
        auto = false;
        try
        {
            using var sc = new ServiceController(ServiceName);
            running = sc.Status == ServiceControllerStatus.Running;
            auto = sc.StartType == ServiceStartMode.Automatic;
            return true;
        }
        catch (InvalidOperationException)
        {
            // 服务不存在。
            return false;
        }
        catch (Exception)
        {
            // 存在但无权限读状态：仍算存在，状态按未知处理。
            return true;
        }
    }

    private static (string Directory, string How) ResolveInstallDirectory()
    {
        var imagePath = ReadServiceImagePath();
        if (imagePath.Length > 0)
        {
            // sunshinesvc.exe 住在 <install>\tools，主程序在上一层；sunshine.exe 直接住在 <install>。
            var containing = Path.GetDirectoryName(imagePath) ?? string.Empty;
            var parent = Path.GetDirectoryName(containing) ?? string.Empty;
            foreach (var candidate in new[] { containing, parent })
            {
                if (candidate.Length > 0 && File.Exists(Path.Combine(candidate, "sunshine.exe")))
                {
                    return (candidate, $"由服务 ImagePath 定位到 {candidate}");
                }
            }
        }

        foreach (var candidate in KnownInstallCandidates())
        {
            if (File.Exists(Path.Combine(candidate, "sunshine.exe")))
            {
                return (candidate, $"由已知安装目录探测到 {candidate}");
            }
        }

        return (DefaultInstallDirectory(), "未找到 sunshine.exe，按默认安装目录处理");
    }

    private static string ReadServiceImagePath()
    {
        try
        {
            using var HKLM = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var service = HKLM.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var raw = service?.GetValue("ImagePath") as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            return Unquote(Environment.ExpandEnvironmentVariables(raw.Trim()));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string Unquote(string path)
    {
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            return path[1..^1];
        }

        // 常见形如 "C:\...\sunshinesvc.exe" /service，取引号段。
        var firstQuote = path.IndexOf('"');
        if (firstQuote >= 0)
        {
            var end = path.IndexOf('"', firstQuote + 1);
            if (end > firstQuote)
            {
                return path[(firstQuote + 1)..end];
            }
        }

        return path;
    }

    private static IEnumerable<string> KnownInstallCandidates()
    {
        yield return DefaultInstallDirectory();

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(programFilesX86))
        {
            yield return Path.Combine(programFilesX86, "Sunshine");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localAppData))
        {
            yield return Path.Combine(localAppData, "Programs", "Sunshine");
        }
    }

    private static string DefaultInstallDirectory()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return Path.Combine(string.IsNullOrEmpty(programFiles) ? @"C:\Program Files" : programFiles, "Sunshine");
    }

    private static (string Directory, string Source) ResolveConfigDirectory(string installDirectory)
    {
        var candidates = new List<string>();
        foreach (var relative in ConfigDirCandidateRelPaths)
        {
            candidates.Add(Path.Combine(installDirectory, relative));
        }

        // 非服务安装（可移植版）把配置放在用户目录，Session 0 下即 systemprofile。
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Sunshine", "config"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "sunshine"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(candidate, "sunshine.conf")))
            {
                return (candidate, candidate);
            }
        }

        // 没有 sunshine.conf 时仍返回首选目录，让上层如实报“配置文件不存在”。
        return (candidates[0], string.Empty);
    }

    private static string ReadVersion(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion ?? string.Empty : info.FileVersion;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
