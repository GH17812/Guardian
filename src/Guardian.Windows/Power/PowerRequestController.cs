using Guardian.Core.Ports;
using Guardian.Windows.Interop;

namespace Guardian.Windows.Power;

/// <summary>
/// 电源请求控制器：基于 Windows 官方执行状态 API（SetThreadExecutionState）。
/// - 专用线程持有请求：线程存活期间请求有效，线程退出即自动释放（天然 fail-open）；
/// - 不修改用户电源计划；
/// - 定期重申请求，防止系统事件清除执行状态。
/// </summary>
public sealed class PowerRequestController : IPowerRequestController, IDisposable
{
    private static readonly TimeSpan ReassertInterval = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _active;
    private bool _keepDisplayOn;
    private bool _awayMode;
    private bool _stateChanged = true;

    public PowerRequestController()
    {
        _thread = new Thread(ThreadLoop)
        {
            IsBackground = true,
            Name = "Guardian.PowerRequest",
        };
        _thread.Start();
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _active;
            }
        }
    }

    public void Apply(bool keepDisplayOn)
    {
        lock (_sync)
        {
            _active = true;
            _keepDisplayOn = keepDisplayOn;
            _stateChanged = true;
        }
        _wake.Set();
    }

    public void Release()
    {
        lock (_sync)
        {
            _active = false;
            _stateChanged = true;
        }
        _wake.Set();
    }

    /// <summary>
    /// 离开模式（Away Mode）请求：睡眠动作发生时系统不进入 S3，
    /// 而是“屏幕熄灭、音频静音、系统保持运行、网络在线”（PS5 式待机）。
    /// 与保护请求相互独立，并集生效。
    /// </summary>
    public void SetAwayMode(bool enabled)
    {
        lock (_sync)
        {
            if (_awayMode == enabled)
            {
                return;
            }
            _awayMode = enabled;
            _stateChanged = true;
        }
        _wake.Set();
    }

    private void ThreadLoop()
    {
        while (!_stop)
        {
            uint flags;
            bool changed;
            lock (_sync)
            {
                changed = _stateChanged;
                _stateChanged = false;
                flags = ComputeFlagsLocked();
            }

            if (changed || _active || _awayMode)
            {
                TrySetExecutionState(flags);
            }

            _wake.Wait(ReassertInterval);
        }

        // 退出前释放请求（线程退出本身也会由系统自动释放）。
        TrySetExecutionState(Win32Constants.ES_CONTINUOUS);
    }

    private uint ComputeFlagsLocked()
    {
        var flags = Win32Constants.ES_CONTINUOUS;

        if (_active)
        {
            flags |= Win32Constants.ES_SYSTEM_REQUIRED;
            if (_keepDisplayOn)
            {
                flags |= Win32Constants.ES_DISPLAY_REQUIRED;
            }
        }

        if (_awayMode)
        {
            // 离开模式：睡眠动作转为“屏幕熄灭但保持运行”（PS5 式待机）。
            flags |= Win32Constants.ES_SYSTEM_REQUIRED | Win32Constants.ES_AWAYMODE_REQUIRED;
        }

        if (!_active && !_awayMode)
        {
            return Win32Constants.ES_CONTINUOUS;
        }
        return flags;
    }

    private static void TrySetExecutionState(uint flags)
    {
        try
        {
            NativeMethods.SetThreadExecutionState(flags);
        }
        catch
        {
            // 单次重申失败不致命，下一轮周期会重试。
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            // 线程无法及时退出（后台线程，进程退出时会随进程结束，执行状态请求自动释放）。
        }
        _wake.Dispose();
    }
}
