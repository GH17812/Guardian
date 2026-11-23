using Guardian.Windows.Interop;
using Guardian.Windows.Networking;
using Microsoft.Win32;

namespace Guardian.Windows.Display;

/// <summary>
/// 光标边界钳制：Sunshine 的虚拟显示器（VDD/IndirectKmd）在驱动加载后常驻拓扑，
/// 标准 API（ChangeDisplaySettingsEx 等）摘除无效（驱动忽略，实测 rc=0 但屏幕仍在），
/// 导致桌面右侧残留一块不可见扩展区域，光标能移出物理屏边界（用户实测反馈）。
/// 不动显示拓扑（避免干扰 Sunshine），改用 ClipCursor 把光标限制在物理屏并集内：
/// 串流进行中必须释放钳制——远程光标需要在虚拟屏上移动，否则 Moonlight 鼠标会被卡死。
/// 钳制可能被其他进程的 ClipCursor 或 Ctrl+Alt+Del 解除，因此 1 秒轮询保底重设，
/// 显示拓扑变化时立即重算。
/// </summary>
public sealed class CursorBoundaryClip : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly StreamingSessionDetector _streaming = new();
    private readonly Timer _timer;
    private bool _clipped;
    private bool _disposed;

    /// <summary>钳制状态变化日志（供宿主写入诊断文件）。回调来自线程池线程。</summary>
    public event Action<string>? Diagnostics;

    public CursorBoundaryClip()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _timer = new Timer(_ => SafeReevaluate(), null, PollInterval, PollInterval);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => SafeReevaluate();

    private void SafeReevaluate()
    {
        try
        {
            Reevaluate();
        }
        catch
        {
            // 单次计算失败忽略：失败时保持现状（fail-open——钳制失效只是光标又能移出去）。
        }
    }

    private void Reevaluate()
    {
        if (_disposed)
        {
            return;
        }

        // 串流中：释放钳制（远程光标需要虚拟屏）。
        if (_streaming.IsStreamingActive())
        {
            Release();
            return;
        }

        // 无虚拟显示器残留时不需要钳制（多物理屏场景绝不限制光标）。
        var monitors = DisplayTopology.GetActiveMonitors();
        if (!monitors.Any(m => m.IsVirtual))
        {
            Release();
            return;
        }

        var bounds = UnionOfPhysical(monitors);
        if (bounds is null)
        {
            Release();
            return;
        }

        var rect = bounds.Value;
        if (!_clipped)
        {
            Diagnostics?.Invoke($"cursor clipped to physical displays ({rect.Right}x{rect.Bottom})");
        }
        NativeMethods.ClipCursor(ref rect);
        _clipped = true;
    }

    private void Release()
    {
        if (!_clipped)
        {
            return;
        }
        _clipped = false;
        NativeMethods.ClipCursor(IntPtr.Zero);
        Diagnostics?.Invoke("cursor clip released");
    }

    /// <summary>物理显示器（排除虚拟屏）的边界并集；无物理屏返回 null（fail-open）。</summary>
    private static NativeMethods.RECT? UnionOfPhysical(List<DisplayTopology.ActiveMonitor> monitors)
    {
        var physical = monitors.Where(m => !m.IsVirtual).ToList();
        if (physical.Count == 0)
        {
            return null;
        }
        var left = physical.Min(m => m.Monitor.Left);
        var top = physical.Min(m => m.Monitor.Top);
        var right = physical.Max(m => m.Monitor.Right);
        var bottom = physical.Max(m => m.Monitor.Bottom);
        return new NativeMethods.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _timer.Dispose();
        Release();
    }
}
