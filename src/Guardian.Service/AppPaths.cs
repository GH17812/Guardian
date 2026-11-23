using Microsoft.Win32;
using System.Security.Principal;

namespace Guardian.Service;

/// <summary>
/// 运行路径解析：
/// - 以 SYSTEM 身份运行（作为 Windows 服务）：使用 C:\ProgramData\Guardian（数据、日志、数据库）；
/// - 以普通用户身份运行（开发/诊断控制台模式）：使用 %LOCALAPPDATA%\Guardian。
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string rootDirectory, string? installDirectory)
    {
        RootDirectory = rootDirectory;
        InstallDirectory = string.IsNullOrWhiteSpace(installDirectory) ? null : installDirectory;
    }

    public string RootDirectory { get; }

    /// <summary>安装目录（来自 HKLM\SOFTWARE\Guardian\InstallDir），用于 IPC 调用方路径校验；null 表示未安装。</summary>
    public string? InstallDirectory { get; }

    public string DatabasePath => Path.Combine(RootDirectory, "guardian.db");

    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static AppPaths Resolve()
    {
        string root;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            root = identity.IsSystem
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Guardian")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardian");
        }

        string? installDir = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Guardian");
            installDir = key?.GetValue("InstallDir") as string;
        }
        catch
        {
            // 无注册表访问权限时忽略。
        }

        return new AppPaths(root, installDir);
    }
}
