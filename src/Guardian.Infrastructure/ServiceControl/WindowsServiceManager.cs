using System.Diagnostics;
using System.ServiceProcess;
using Guardian.Contracts.Protocol;

namespace Guardian.Infrastructure.ServiceControl;

/// <summary>Guardian 后台服务的查询与控制（供桌面客户端使用）。</summary>
public static class WindowsServiceManager
{
    public static bool IsInstalled()
    {
        try
        {
            return ServiceController.GetServices().Any(s => s.ServiceName.Equals(ProtocolConstants.ServiceName, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>null 表示服务不存在或无法访问。</summary>
    public static ServiceControllerStatus? GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ProtocolConstants.ServiceName);
            return sc.Status;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsRunning()
        => GetStatus() == ServiceControllerStatus.Running;

    /// <summary>尝试启动服务；无权限时返回 false（调用方应走提权路径）。</summary>
    public static bool TryStart()
    {
        try
        {
            using var sc = new ServiceController(ProtocolConstants.ServiceName);
            if (sc.Status != ServiceControllerStatus.Running)
            {
                sc.Start();
            }
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15));
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryStop()
    {
        try
        {
            using var sc = new ServiceController(ProtocolConstants.ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>以管理员权限运行 PowerShell 脚本（触发 UAC）。</summary>
    public static bool RunElevatedPowerShell(string scriptPath, string arguments, out string error)
    {
        error = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {arguments}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal,
            };
            var process = Process.Start(psi);
            process?.WaitForExit(120_000);
            return process is { ExitCode: 0 };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
