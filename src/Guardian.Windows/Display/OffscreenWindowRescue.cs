using System.Text;
using Guardian.Windows.Interop;
using Guardian.Windows.Networking;

namespace Guardian.Windows.Display;

/// <summary>
/// 窗口位置救援：把落在“看不见的区域”的窗口移回主显示器工作区。
/// 背景：串流（Sunshine dd_configuration_option=ensure_only_display）期间虚拟显示器成为主屏、物理屏被停用；
/// 串流结束后拓扑恢复，原本在虚拟屏（本机固定位于 x=3200 的扩展区）上的程序窗口仍保留旧坐标，
/// 落在物理屏之外，表现为“任务栏里有图标、桌面上看不到窗口”，重启程序也不恢复。
/// 判定规则（保守，避免干扰正常多屏）：
/// 1. 只处理落在物理显示器上的可见面积不足一半的顶层窗口（虚拟屏上的窗口、完全离屏或尺寸超出屏幕的窗口）；
/// 2. 串流进行中不做处理（此时物理屏本就被停用，移动窗口只会制造抖动）；
/// 3. 最小化、工具窗口、DWM 已隐藏（cloaked）、无标题、Shell 系统窗口一律跳过；
/// 4. 无法枚举到物理显示器时不做任何处理（fail-open，绝不擅自搬动窗口）。
/// </summary>
public sealed class OffscreenWindowRescue
{
    /// <summary>判定“可用”的最小可见比例：可见面积不足窗口面积一半即视为丢失在看不见的区域。</summary>
    private const double MinVisibleRatio = 0.5;

    private static readonly HashSet<string> SkippedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "ApplicationFrameWindow",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "MultitaskingViewFrame",
    };

    private readonly StreamingSessionDetector _streaming = new();

    /// <summary>执行一次救援，返回被移回主屏的窗口数量。</summary>
    public int Rescue()
    {
        // 统一使用物理像素坐标：宿主进程 DPI 感知模式不同会导致 GetWindowRect/SetWindowPos 被虚拟化。
        var previousDpi = NativeMethods.SetThreadDpiAwarenessContext(
            new IntPtr(Win32Constants.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2));
        try
        {
            return RescueCore();
        }
        catch
        {
            // 救援失败不影响主流程。
            return 0;
        }
        finally
        {
            if (previousDpi != IntPtr.Zero)
            {
                NativeMethods.SetThreadDpiAwarenessContext(previousDpi);
            }
        }
    }

    private int RescueCore()
    {
        if (_streaming.IsStreamingActive())
        {
            return 0;
        }

        var monitors = DisplayTopology.GetActiveMonitors();
        var physical = monitors.Where(m => !m.IsVirtual).ToList();
        if (physical.Count == 0)
        {
            return 0;
        }

        var work = (monitors.FirstOrDefault(m => m.IsPrimary && !m.IsVirtual) ?? physical[0]).Work;

        var moved = 0;
        NativeMethods.EnumWindowsProc callback = (hwnd, _) =>
        {
            if (TryRescue(hwnd, physical, work))
            {
                moved++;
            }
            return true;
        };
        NativeMethods.EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return moved;
    }

    private static bool TryRescue(IntPtr hwnd, IReadOnlyList<DisplayTopology.ActiveMonitor> physical, NativeMethods.RECT work)
    {
        if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd))
        {
            return false;
        }
        if ((NativeMethods.GetWindowLong(hwnd, Win32Constants.GWL_EXSTYLE) & Win32Constants.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }
        if (IsCloaked(hwnd))
        {
            return false;
        }
        if (!NativeMethods.GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }
        if (GetVisibleRatio(rect, physical) >= MinVisibleRatio && !IsOversized(rect, work))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(GetWindowText(hwnd)))
        {
            return false;
        }
        if (SkippedClasses.Contains(GetClassName(hwnd)))
        {
            return false;
        }

        return MoveToWorkArea(hwnd, rect, work);
    }

    private static bool MoveToWorkArea(IntPtr hwnd, NativeMethods.RECT rect, NativeMethods.RECT work)
    {
        var maximized = NativeMethods.IsZoomed(hwnd);
        if (maximized)
        {
            // 最大化窗口的坐标由系统管理，须先还原再定位。
            NativeMethods.ShowWindow(hwnd, Win32Constants.SW_RESTORE);
        }

        var width = Math.Min(rect.Width, work.Width * 9 / 10);
        var height = Math.Min(rect.Height, work.Height * 9 / 10);
        var x = work.Left + Math.Max(0, (work.Width - width) / 2);
        var y = work.Top + Math.Max(0, (work.Height - height) / 2);

        var ok = NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            width,
            height,
            Win32Constants.SWP_NOZORDER | Win32Constants.SWP_NOACTIVATE | Win32Constants.SWP_ASYNCWINDOWPOS);

        if (maximized)
        {
            NativeMethods.ShowWindow(hwnd, Win32Constants.SW_MAXIMIZE);
        }
        return ok;
    }

    /// <summary>非最大化窗口的尺寸超出工作区（拓扑切换后可能出现的异常状态）。</summary>
    private static bool IsOversized(NativeMethods.RECT rect, NativeMethods.RECT work)
        => rect.Width > work.Width + 8 || rect.Height > work.Height + 8;

    /// <summary>窗口面积中落在物理显示器上的比例。</summary>
    private static double GetVisibleRatio(NativeMethods.RECT rect, IReadOnlyList<DisplayTopology.ActiveMonitor> physical)
    {
        var area = (long)rect.Width * rect.Height;
        if (area <= 0)
        {
            return 1;
        }

        long visible = 0;
        foreach (var monitor in physical)
        {
            var overlapWidth = Math.Min(rect.Right, monitor.Monitor.Right) - Math.Max(rect.Left, monitor.Monitor.Left);
            var overlapHeight = Math.Min(rect.Bottom, monitor.Monitor.Bottom) - Math.Max(rect.Top, monitor.Monitor.Top);
            if (overlapWidth > 0 && overlapHeight > 0)
            {
                visible += (long)overlapWidth * overlapHeight;
            }
        }
        return (double)visible / area;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.DwmGetWindowAttribute(
                hwnd, Win32Constants.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
