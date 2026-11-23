using System.Security.Principal;
using System.Text;
using Guardian.Infrastructure.Interop;

namespace Guardian.Infrastructure.Ipc;

/// <summary>
/// 调用方身份校验：
/// 1. 通过管道句柄取得客户端进程 PID；
/// 2. 查询进程映像路径，与授权清单（受信任进程名 / 安装目录）比对；
/// 3. 检查调用方是否处于提权状态（管理员工具场景）。
/// </summary>
public static class CallerVerifier
{
    /// <summary>获取管道客户端进程的完整映像路径；失败返回 null。</summary>
    public static string? GetClientProcessPath(IntPtr pipeHandle)
    {
        if (!NativeMethods.GetNamedPipeClientProcessId(pipeHandle, out var pid))
        {
            return null;
        }
        return GetProcessPath(pid);
    }

    public static string? GetProcessPath(uint pid)
    {
        var handle = NativeMethods.OpenProcess(NativeConstants.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>调用方进程是否处于提权（管理员）状态。</summary>
    public static bool IsProcessElevated(uint pid)
    {
        var process = NativeMethods.OpenProcess(NativeConstants.PROCESS_QUERY_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
        {
            return false;
        }
        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeConstants.TOKEN_QUERY, out var token))
            {
                return false;
            }
            try
            {
                return NativeMethods.GetTokenInformation(
                    token,
                    NativeConstants.TokenElevation,
                    out var elevation,
                    (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TOKEN_ELEVATION>(),
                    out _)
                    && elevation.TokenIsElevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    /// <summary>
    /// 综合判定：受信任进程名、安装目录内进程，或提权调用方。
    /// <paramref name="allowlistedNames"/> 为受信任可执行文件名集合（如 Guardian.App.exe）。
    /// <paramref name="trustedRoot"/> 为安装根目录（可为 null）。
    /// </summary>
    public static bool IsAuthorized(uint pid, IReadOnlyCollection<string> allowlistedNames, string? trustedRoot)
    {
        var path = GetProcessPath(pid);
        if (path is not null)
        {
            var name = Path.GetFileName(path);
            if (allowlistedNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!string.IsNullOrEmpty(trustedRoot)
                && path.StartsWith(trustedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return IsProcessElevated(pid);
    }
}
