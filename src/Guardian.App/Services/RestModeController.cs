using System.Windows;
using System.Windows.Interop;
using Guardian.Windows.Interop;
using Guardian.Windows.Networking;
using Guardian.Windows.Power;

namespace Guardian.App.Services;

/// <summary>
/// 待机模式客户端状态机（PS5 式待机）：
/// 服务端负责离开模式电源请求（睡眠转为“屏幕熄灭、系统在线”），本类负责交互会话内的
/// 待机会话（屏幕熄灭）与唤醒（点亮 + 锁屏凭证）。
/// 优先级规则：
/// 1. 保护 > 待机：隐私保护生效期间本地输入不唤醒（屏幕保持熄灭）；
/// 2. 串流 > 待机：串流期间绝不唤醒/锁屏（注入输入已按 INJECTED 过滤，物理输入也忽略，
///    避免锁屏打断 Sunshine 捕获）；
/// 3. 输入隔离下本地输入被拦，唯一本地逃生通道是紧急快捷键（触发全面恢复并退出待机）。
/// 退出待机需要凭证：唤醒即 LockWorkStation，密码/PIN 由 Windows 校验，本程序不接触凭据。
/// </summary>
public sealed class RestModeController : IDisposable
{
    private static readonly TimeSpan StreamingPollInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 进入会话后的输入宽容期：开启待机的那一下点击（勾选框）会有残留的
    /// 鼠标松开/移动事件，若立即按“物理输入唤醒”处理会瞬间锁屏。
    /// 宽容期内吞掉全部物理输入（不锁屏）——吞掉比放行安全：放行会穿透到
    /// Guardian 勾选框，可能把刚点亮的开关又点掉。
    /// </summary>
    private static readonly TimeSpan SessionGracePeriod = TimeSpan.FromSeconds(3);

    private readonly Action<bool> _setDisplayRest;
    private readonly Action _onEmergency;
    private readonly Action<bool> _onSessionStateChanged;
    private readonly Action<bool> _onStreamingStateChanged;
    private readonly StreamingSessionDetector _streaming = new();
    private readonly RestWakeDetector _detector = new();

    private bool _armed;
    private bool _session;
    private bool _awayModeEngaged;
    private bool _guardActive;
    private bool _streamingActive;
    private long _sessionEnteredAt;
    private IntPtr _awayNotify;
    private Timer? _streamingTimer;

    /// <summary>待机会话/武装状态变化（UI 刷新文案）。回调可能来自钩子线程，订阅方须自行调度。</summary>
    public event Action? StateChanged;

    public RestModeController(
        Action<bool> setDisplayRest,
        Action onEmergency,
        Action<bool> onSessionStateChanged,
        Action<bool> onStreamingStateChanged)
    {
        _setDisplayRest = setDisplayRest;
        _onEmergency = onEmergency;
        _onSessionStateChanged = onSessionStateChanged;
        _onStreamingStateChanged = onStreamingStateChanged;
        _detector.PhysicalInput += OnPhysicalInput;
        _detector.HookFailed += ex => App.DiagLog($"[rest] hook install FAILED: {ex.Message}");
        _detector.Diagnostics += msg => App.DiagLog($"[rest] {msg}");
        // 紧急组合键由待机钩子自备识别（吞键期间 RegisterHotKey 可能不触发），后台派发不阻塞钩子。
        _detector.Emergency += () =>
        {
            App.DiagLog("hotkey PRESSED (hook)");
            Task.Run(_onEmergency);
        };
    }

    /// <summary>更新紧急组合键主键（与 EmergencyHotkeyService 保持同步）。</summary>
    public void SetEmergencyVk(int vk) => _detector.SetEmergencyVk(vk);

    /// <summary>当前紧急组合键主键（UI 轮询通道用）。</summary>
    public int EmergencyVk => _detector.EmergencyVk;

    /// <summary>用户是否正在物理交互（最近 1 秒内有本地键鼠输入）。屏幕点亮时用户会操作。</summary>
    public bool IsUserActive
        => Environment.TickCount64 - _detector.LastInputTick < 1000;

    public bool IsArmed => _armed;

    /// <summary>是否处于待机会话（屏幕应熄灭）。含系统离开模式（合盖进入）。</summary>
    public bool IsSessionActive => _session || _awayModeEngaged;

    /// <summary>把电源通知挂接到主窗口（离开模式 / 睡眠恢复检测）。</summary>
    public void AttachWindow(Window window)
    {
        var source = (HwndSource)PresentationSource.FromVisual(window);
        source.AddHook(WndProc);
        _awayNotify = RestModeSupport.RegisterAwayModeNotification(new WindowInteropHelper(window).Handle);
    }

    /// <summary>
    /// 按服务端快照同步武装状态（服务端是权威）。
    /// <paramref name="enterSessionOnArm"/> 必须是 false 的唯一场景：进程刚启动、武装状态是从上次运行
    /// 持久化恢复来的。那种情况下直接把屏幕熄灭等于"打开应用就黑屏"（实测：开机启动/重启应用后
    /// 17:59:52.126 armed → .127 session entered (display off)）。用户主动勾选时才立刻进待机会话。
    /// </summary>
    public void SetArmed(bool armed, bool enterSessionOnArm = true)
    {
        if (_armed == armed)
        {
            return;
        }
        _armed = armed;
        if (armed)
        {
            App.DiagLog($"[rest] armed{(enterSessionOnArm ? string.Empty : " (restored, session kept off)")}");
            if (enterSessionOnArm)
            {
                EnterSession();
            }
            StartStreamingWatch();
            UpdateDetector();
        }
        else
        {
            App.DiagLog("[rest] disarmed");
            UpdateDetector();
            StopStreamingWatch();
            _awayModeEngaged = false;
            if (_session)
            {
                _session = false;
                // 武装解除：恢复显示（fail-open，不锁屏——不是用户主动唤醒）。
                _setDisplayRest(false);
                ReportSessionState(false);
            }
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// 保护生效期间也运行输入钩子（吞掉会点亮 DPMS 显示器的物理输入，防“按空格闪屏”），
    /// 同时紧急组合键由钩子自备识别——不依赖 RegisterHotKey（窗口消息泵卡死/热键被
    /// 抢占时全局热键会失效，实测紧急键无反应的根因之一）。
    /// </summary>
    public void SetProtectionGuard(bool active)
    {
        if (_guardActive == active)
        {
            return;
        }
        _guardActive = active;
        App.DiagLog($"[rest] protection guard {(active ? "on" : "off")} (hook {(active || _armed ? "running" : "stopped")})");
        UpdateDetector();
    }

    private void UpdateDetector()
    {
        if (_armed || _guardActive)
        {
            _detector.Start();
        }
        else
        {
            _detector.Stop();
        }
    }

    /// <summary>进入待机会话（开关开启即待机；保护解除后恢复待机）。</summary>
    public void EnterSession()
    {
        if (!_armed || _session)
        {
            return;
        }
        _session = true;
        _sessionEnteredAt = Environment.TickCount64;
        App.DiagLog("[rest] session entered (display off)");
        _setDisplayRest(true);
        ReportSessionState(true);
        StateChanged?.Invoke();
    }

    /// <summary>紧急快捷键：若正在待机则唤醒并锁屏（保护恢复流程由调用方另行处理）。</summary>
    public void OnEmergency()
    {
        if (!_armed)
        {
            return;
        }
        if (_session || _awayModeEngaged)
        {
            ExitSessionAndLock();
        }
    }

    /// <summary>唤醒待机会话：立即锁屏（凭证），显示点亮放后台执行。</summary>
    private void ExitSessionAndLock()
    {
        _session = false;
        _awayModeEngaged = false;
        App.DiagLog("[rest] wake: lock workstation + display on");
        RestModeSupport.LockWorkstation();
        // 会话结束即上报：服务端释放 Away Mode（离开模式静音音频，进桌面后必须恢复声音）。
        ReportSessionState(false);
        StateChanged?.Invoke();
        _ = Task.Run(() => _setDisplayRest(false));
    }

    /// <summary>
    /// 物理输入到达（钩子线程，必须快速返回）：判定是否吞掉/唤醒。
    /// 返回 true = 吞掉该事件。保护生效期间吞掉全部物理输入——输入会点亮
    /// DPMS 熄灭的显示器（造成“亮→3 秒后重申变黑”的闪屏），吞掉后屏幕保持熄灭。
    /// </summary>
    private bool OnPhysicalInput()
    {
        // 重写后的输入策略：保护场景（未武装待机）不再吞键。
        // 吞键曾带来：卡键（修饰键抬起被吞）、串流干扰、误吞紧急键、低层钩子超时风险。
        // 防闪屏改由 DisplayPrivacyClient 的“输入即压黑”（50ms 响应）承担；
        // 防误点开关改由“保护开启后窗口自动最小化”承担。
        // 钩子在 guard 模式下仅承担紧急键识别。
        if (!_armed)
        {
            return false;
        }
        if (_streaming.IsStreamingActive())
        {
            // 串流期间不唤醒/不锁屏（锁屏会打断 Sunshine 捕获），且物理屏
            // 已被 Sunshine 停用——本地输入无隐私风险，放行。
            return false;
        }
        if (_session && Environment.TickCount64 - _sessionEnteredAt < (long)SessionGracePeriod.TotalMilliseconds)
        {
            // 刚进入会话的宽容期：点击开关的残留鼠标事件会立即触发“唤醒锁屏”。
            // 吞掉（不放行——放行会穿透到勾选框把开关点掉），不锁屏。
            return true;
        }
        if (!_session && !_awayModeEngaged)
        {
            // 用户正在桌面工作。
            return false;
        }

        ExitSessionAndLock();
        return true;
    }

    /// <summary>上报待机会话状态给服务端（Away Mode 的实际持有者是服务端）。</summary>
    private void ReportSessionState(bool active)
    {
        try
        {
            _onSessionStateChanged(active);
        }
        catch
        {
            // 服务不可达时静默；下次状态变化会重试。
        }
    }

    /// <summary>
    /// 串流轮询：串流期间服务端必须暂停 Away Mode（离开模式静音音频端点，
    /// 会导致 Sunshine 音频流建立失败、整条连接中断）。仅武装时轮询。
    /// </summary>
    private void StartStreamingWatch()
    {
        _streamingTimer ??= new Timer(
            _ => SafePollStreaming(),
            null,
            StreamingPollInterval,
            StreamingPollInterval);
    }

    private void StopStreamingWatch()
    {
        _streamingTimer?.Dispose();
        _streamingTimer = null;
        _streamingActive = false;
    }

    private void SafePollStreaming()
    {
        try
        {
            // 串流状态：变化即上报（服务端据此暂停/恢复 Away Mode，保音频）。
            var streaming = _streaming.IsStreamingActive();
            if (streaming != _streamingActive)
            {
                _streamingActive = streaming;
                App.DiagLog($"[rest] streaming {(streaming ? "active" : "ended")}: report to service");
                _onStreamingStateChanged(streaming);
            }

            // 会话状态：每轮幂等重报（服务端同值直接 Ok）。唤醒上报若因服务
            // 不可达失败，最多 1 秒后自动纠正——绝不让系统停留在离开模式
            // （音频静音、图形栈半挂起，实测表现为解锁后巨卡）。
            if (_armed)
            {
                ReportSessionState(_session);
            }
        }
        catch
        {
            // 单次检测失败忽略，下一轮重试。
        }
    }

    private void OnAwayModeSetting(bool engaged)
    {
        if (!_armed)
        {
            return;
        }
        // 只记录系统离开模式状态（合盖进入待机的场景由 _awayModeEngaged 承担唤醒判定），
        // 不修改 _session——避免系统电源事件覆盖“用户已唤醒”的意图（曾导致解锁后
        // 又重进会话、输入被吞、画面巨卡）。
        _awayModeEngaged = engaged;
        if (engaged)
        {
            StateChanged?.Invoke();
        }
        App.DiagLog($"[rest] away mode {(engaged ? "engaged" : "exited")}");
    }

    private void OnResumeFromSuspend()
    {
        if (!_armed)
        {
            return;
        }
        App.DiagLog("[rest] resume from suspend while armed: lock");
        ExitSessionAndLock();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RestModeSupport.WM_POWERBROADCAST)
        {
            if (wParam.ToInt32() == RestModeSupport.PBT_POWERSETTINGCHANGE
                && RestModeSupport.TryParseAwayModeSetting(lParam, out var engaged))
            {
                OnAwayModeSetting(engaged);
            }
            else if (wParam.ToInt32() == RestModeSupport.PBT_APMRESUMEAUTOMATIC)
            {
                OnResumeFromSuspend();
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        StopStreamingWatch();
        _detector.Dispose();
        RestModeSupport.UnregisterNotification(_awayNotify);
    }
}
