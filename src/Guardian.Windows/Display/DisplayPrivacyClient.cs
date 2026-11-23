using System.Runtime.InteropServices;
using Guardian.Windows.Interop;
using Guardian.Windows.Networking;
using Microsoft.Win32;

namespace Guardian.Windows.Display;

/// <summary>
/// 显示隐私客户端执行器：在交互会话（用户进程）内执行显示器关闭/恢复。
/// 背景：服务运行于 Session 0，跨会话广播不可靠；由客户端按服务端快照执行，快照仍是唯一权威。
/// 两个独立的“熄灭源”：隐私保护 + 待机会话。任一熄灭源生效即保持屏幕关闭，
/// 全部解除才恢复显示。串流期间（Sunshine 活跃）不发送任何电源广播（见下）。
/// 两级策略：
/// 1. 优先 DDC/CI 背光关闭（VCP_POWER soft-off）—— 系统电源状态不变，串流软件不会重新点亮，
///    输入不会唤醒，串流端无切换抖动；
/// 2. 回退 SC_MONITORPOWER 广播（显示器不支持 DDC/CI 时）。
/// 串流兼容（Sunshine/Moonlight 等）：
/// SC_MONITORPOWER 关屏会让 GPU 显示输出复位，破坏串流端的帧捕获，表现为光标卡顿/瞬移、
/// 无法建立会话、断开后无法重连。因此检测到活跃串流连接时立即暂停关屏，期间**不做任何开屏动作**
/// （物理屏马上会被 Sunshine 虚拟显示器方案停用，此时广播开屏只会造成“开屏→再被拔掉”的
/// 电源切换，正是串流启动瞬间卡顿的元凶）；串流结束后经宽限期再恢复压黑。
/// 取消保护时的恢复不可靠（MONITOR_ON 广播若落在 Sunshine 拓扑恢复窗口内会失效），
/// 因此恢复采用“立即 + 多次重试 + 鼠标微动兜底”策略，确保物理屏最终点亮；
/// 若串流仍在进行，则推迟到串流结束后再唤醒——串流期间的任何电源广播
/// 都会干扰 Sunshine 帧捕获（残留卡顿的来源），全程保持静默。
/// 此降级符合 fail-open 原则（可用性优先），界面磁贴同步显示“暂缓”，保证状态如实呈现。
/// fail-open：客户端崩溃后重申停止，显示器可恢复；服务端恢复时快照置 false，客户端随即恢复。
/// </summary>
public sealed class DisplayPrivacyClient : IDisposable
{
    private static readonly TimeSpan DdcReassertInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FallbackReassertInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StreamingPollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan StreamingGracePeriod = TimeSpan.FromSeconds(10);

    /// <summary>串流结束后的唤醒窗口：此窗口内的唤醒附带物理显示器重新挂载兜底。</summary>
    private static readonly TimeSpan ReattachWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// 推迟唤醒的最长等待：检测器可能因物理屏长时间缺席/TCP 残留而一直判定“串流中”，
    /// 超时后强制唤醒（fail-open），绝不让屏幕无限期黑着。
    /// </summary>
    private static readonly TimeSpan WakeDeferLimit = TimeSpan.FromSeconds(20);

    private readonly object _sync = new();
    private readonly StreamingSessionDetector _streaming = new();
    private Timer? _reassertTimer;
    private Timer? _streamingTimer;
    private bool _privacyActive;
    private bool _restSession;
    private bool _active;
    private bool _ddcMode;
    private bool _ddcProbed;
    private bool _suppressed;
    private bool _wakePending;
    private DateTime? _wakeDeferredAt;
    private DateTime? _streamingEndedAt;
    private DateTime? _lastStreamingSessionEndedAt;
    private int _wakeGeneration;
    private Func<bool> _isUserActive = () => false;
    private bool _windowVisible;
    private long _lastInputFlashOff;
    private long _displayRequiredUntil;
    private Thread? _displayHoldThread;
    private readonly Timer _inputWatchTimer;

    /// <summary>串流暂缓状态变化（true=因串流暂缓关屏）。回调可能来自线程池线程，订阅方需自行调度到 UI。</summary>
    public event Action<bool>? SuppressionChanged;

    /// <summary>诊断日志（关屏/恢复/暂缓等状态迁移），供宿主进程写入诊断文件。</summary>
    public event Action<string>? Diagnostics;

    /// <summary>
    /// 注入“用户活动”探针（本地物理键鼠输入）。屏幕点亮后用户会开始操作——
    /// 唤醒重试/物理屏重挂载在此之后继续执行只会干扰用户（实测解锁后桌面卡 10 秒的根因）。
    /// </summary>
    public void SetUserActivityProbe(Func<bool> probe) => _isUserActive = probe ?? (() => false);

    /// <summary>
    /// 会话解锁 = 屏幕必然已点亮且用户已进入桌面：取消全部在途唤醒重试，
    /// 6/15/30 秒的重试广播若打在已解锁的桌面上会反复复位显示管线（实测 10 秒卡顿来源之一）。
    /// </summary>
    public DisplayPrivacyClient()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
        // 常驻输入观察：50ms 轮询系统输入活动，检测到面板被输入点亮立即压黑
        // （替代吞键防闪屏——吞键带来卡键/串流干扰/误吞紧急键，重写后弃用）。
        _inputWatchTimer = new Timer(_ => SafeWatchInputFlash(), null, 50, 50);
    }

    /// <summary>
    /// 输入即压黑观察：保护生效、非暂缓、窗口隐藏时，若检测到**物理输入活动**
    /// （物理键鼠点亮 DPMS 面板），立即广播 MONITOR_OFF——闪烁从 3 秒重申
    /// 周期缩短到 50~100ms，肉眼几乎无感。300ms 节流防重复广播。
    /// 信号源为钩子的物理输入时间戳（INJECTED 过滤）：串流端注入输入**不触发**
    /// ——此前用系统级 GetLastInputInfo（不分来源），串流启动的检测延迟窗口内
    /// 注入输入触发高频关屏广播，打在 Sunshine 捕获链路（串流端卡顿/光标闪烁）。
    /// </summary>
    private void SafeWatchInputFlash()
    {
        try
        {
            lock (_sync)
            {
                if (!_active || _suppressed || _windowVisible)
                {
                    return;
                }
                if (!_isUserActive())
                {
                    return; // 无近期物理输入（注入输入不计入）。
                }
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref _lastInputFlashOff) < 300)
                {
                    return; // 节流：300ms 内不重复广播。
                }
                Interlocked.Exchange(ref _lastInputFlashOff, now);
                Log("physical input: immediate reblack");
                TryTurnOffLocked();
            }
        }
        catch
        {
            // 单次观察失败忽略。
        }
    }

    /// <summary>
    /// Guardian 主窗口可见性：可见 = 用户正在操作界面（屏幕应点亮），隐藏/最小化 = 压黑。
    /// 可见时暂缓压黑并点亮；隐藏时恢复压黑（fail-open 方向：窗口状态如实对应屏幕状态）。
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        bool? suppressionChange = null;
        lock (_sync)
        {
            if (_windowVisible == visible)
            {
                return;
            }
            _windowVisible = visible;
            if (!_active)
            {
                return;
            }
            if (visible)
            {
                if (!_suppressed)
                {
                    _suppressed = true;
                    suppressionChange = true;
                    Log("window visible: display wake (suppressed)");
                    TryTurnOnLocked();
                }
            }
            else if (_suppressed && !IsStreamingLocked())
            {
                _suppressed = false;
                suppressionChange = false;
                Log("window hidden: reblack");
                TryTurnOffLocked();
                EnsureReassertLocked();
            }
        }
        RaiseSuppressionChanged(suppressionChange);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason != SessionSwitchReason.SessionUnlock)
        {
            return;
        }
        lock (_sync)
        {
            if (_wakeGeneration == 0)
            {
                return;
            }
            _wakeGeneration++;
            Log("wake retries cancelled: session unlocked");
        }
    }

    public bool IsSuppressed
    {
        get
        {
            lock (_sync)
            {
                return _suppressed;
            }
        }
    }

    /// <summary>按服务端快照同步隐私保护。true=保持关闭，false=解除。幂等，可随心跳重复调用。</summary>
    public void Apply(bool privacyActive)
    {
        bool? suppressionChange;
        lock (_sync)
        {
            _privacyActive = privacyActive;
            suppressionChange = RecomputeLocked();
        }
        RaiseSuppressionChanged(suppressionChange);
    }

    /// <summary>
    /// 待机会话（PS5 式待机）：待机期间屏幕保持熄灭；解除待机会话时才恢复显示。
    /// 与隐私保护相互独立，任一熄灭源生效即保持屏幕关闭。
    /// </summary>
    public void SetRestSession(bool restActive)
    {
        bool? suppressionChange;
        lock (_sync)
        {
            _restSession = restActive;
            suppressionChange = RecomputeLocked();
        }
        RaiseSuppressionChanged(suppressionChange);
    }

    /// <summary>
    /// 显示拓扑变化（如 Sunshine 串流结束重新挂载物理屏）：面板在挂载瞬间会被点亮，
    /// 若此刻仍处于熄灭状态（保护/待机）且无串流，立即重申关屏——
    /// 把“先亮屏、宽限期后再变黑”的过渡闪烁缩短到约百毫秒（丝滑待机）。
    /// 串流进行中（TCP 仍连接）保持静默，绝不影响捕获。
    /// </summary>
    public void OnDisplayTopologyChanged()
    {
        bool? suppressionChange = null;
        lock (_sync)
        {
            if (!_active)
            {
                // 拓扑变化即“串流结束”的最快信号：被推迟的唤醒立即执行，
                // 不能依赖轮询检测（虚拟显示器会话后检测器可能长期误判串流中）。
                if (_wakePending)
                {
                    _wakePending = false;
                    _wakeDeferredAt = null;
                    _lastStreamingSessionEndedAt = DateTime.UtcNow;
                    StopStreamingWatchLocked();
                    Log("topology changed while wake pending: immediate wake");
                    WakeDisplayLocked();
                    ScheduleWakeRetriesLocked();
                }
                return;
            }
            if (IsStreamingLocked())
            {
                // 串流进行中/启动中：保持静默（电源广播会破坏帧捕获）。
                return;
            }

            if (_suppressed)
            {
                // 串流刚结束（物理屏正在恢复）：跳过宽限期，立即恢复压黑。
                _suppressed = false;
                _streamingEndedAt = null;
                _lastStreamingSessionEndedAt = DateTime.UtcNow; // 标记串流刚结束：后续唤醒附带重挂载兜底。
                suppressionChange = false;
                Log("topology changed: streaming ended, immediate reblack");
            }
            else
            {
                Log("topology changed: re-assert display off");
            }
            TryTurnOffLocked();
            EnsureReassertLocked();
        }
        RaiseSuppressionChanged(suppressionChange);
    }

    /// <summary>按“隐私 + 待机”两个熄灭源重新计算执行状态；返回串流暂缓是否变化。</summary>
    private bool? RecomputeLocked()
    {
        var shouldBeActive = _privacyActive || _restSession;
        if (shouldBeActive == _active)
        {
            return null;
        }

        _active = shouldBeActive;
        _wakeGeneration++; // 使任何在途的恢复重试失效。

        if (_active)
        {
            // 进入熄灭：串流中直接暂缓（Sunshine 已接管屏幕），否则立即关屏。
            if (IsStreamingLocked())
            {
                _suppressed = true;
                Log($"display off requested ({ActiveSourceName()}), streaming: suppressed, no power change");
                return true;
            }
            if (_windowVisible)
            {
                // Guardian 窗口可见 = 用户正在操作界面：暂缓压黑并保持点亮。
                _suppressed = true;
                Log($"display off requested ({ActiveSourceName()}), window visible: suppressed");
                TryTurnOnLocked();
                return true;
            }

            Log($"display off ({ActiveSourceName()})");
            TryTurnOffLocked();
            StartStreamingWatchLocked();
            EnsureReassertLocked();
            return null;
        }

        // 全部熄灭源解除：恢复显示。
        StopStreamingWatchLocked();
        _reassertTimer?.Dispose();
        _reassertTimer = null;
        var wasSuppressed = _suppressed;
        _suppressed = false;

        if (IsStreamingLocked())
        {
            // 串流进行中：物理屏被 Sunshine 停用，此刻广播开屏无效且会干扰串流帧捕获。
            // 推迟到串流结束后再唤醒。
            _wakePending = true;
            _wakeDeferredAt = DateTime.UtcNow;
            Log("display wake deferred (streaming)");
            StartStreamingWatchLocked();
        }
        else
        {
            _wakePending = false;
            _wakeDeferredAt = null;
            Log("display wake");
            TryTurnOnLocked();
            ScheduleWakeRetriesLocked();
        }

        return wasSuppressed ? false : null;
    }

    private string ActiveSourceName()
        => (_privacyActive, _restSession) switch
        {
            (true, true) => "privacy+rest",
            (true, false) => "privacy",
            _ => "rest",
        };

    private void RaiseSuppressionChanged(bool? suppressionChange)
    {
        if (suppressionChange is { } changed)
        {
            SuppressionChanged?.Invoke(changed);
        }
    }

    private void StartStreamingWatchLocked()
    {
        _streamingTimer ??= new Timer(
            _ => SafeWatchStreaming(),
            null,
            StreamingPollInterval,
            StreamingPollInterval);
    }

    private void StopStreamingWatchLocked()
    {
        _streamingTimer?.Dispose();
        _streamingTimer = null;
        _streamingEndedAt = null;
    }

    private void SafeWatchStreaming()
    {
        bool? suppressionChange = null;
        lock (_sync)
        {
            if (!_active)
            {
                // 保护已关闭但存在被推迟的唤醒：等串流结束、物理屏恢复后再点亮。
                // 检测器可能长期误判串流中（物理屏缺席/TCP 残留），超过 WakeDeferLimit 后强制唤醒。
                if (_wakePending)
                {
                    var streaming = IsStreamingLocked();
                    var expired = _wakeDeferredAt is { } at && DateTime.UtcNow - at >= WakeDeferLimit;
                    if (!streaming || expired)
                    {
                        _wakePending = false;
                        _wakeDeferredAt = null;
                        _lastStreamingSessionEndedAt = DateTime.UtcNow;
                        StopStreamingWatchLocked();
                        Log(streaming ? "wake defer timeout: force wake" : "stream ended: deferred wake");
                        WakeDisplayLocked();
                        ScheduleWakeRetriesLocked();
                    }
                }
                return;
            }

            if (IsStreamingLocked())
            {
                _streamingEndedAt = null;
                if (!_suppressed)
                {
                    _suppressed = true;
                    suppressionChange = true;
                    _reassertTimer?.Dispose();
                    _reassertTimer = null;
                    // 注意：此处**不**广播开屏。物理屏马上会被 Sunshine 停用（虚拟显示器方案），
                    // 开屏只会造成“开屏→再被拔掉”的电源切换，正是串流启动瞬间卡顿/光标瞬移的元凶。
                    Log("streaming detected: suppressed (no power change)");
                }
            }
            else if (_suppressed)
            {
                if (_windowVisible)
                {
                    // 窗口可见优先：不压黑（用户正在操作界面）。
                    _streamingEndedAt = null;
                    return;
                }
                // 宽限期：串流短暂断开（如切换画质/重连）时不立即压黑，避免打断重连。
                _streamingEndedAt ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _streamingEndedAt.Value >= StreamingGracePeriod)
                {
                    _suppressed = false;
                    suppressionChange = false;
                    _lastStreamingSessionEndedAt = DateTime.UtcNow;
                    Log("streaming ended: grace expired, reblack");
                    TryTurnOffLocked();
                    EnsureReassertLocked();
                }
            }
            else
            {
                EnsureReassertLocked();
            }
        }

        if (suppressionChange is { } changed)
        {
            SuppressionChanged?.Invoke(changed);
        }
    }

    private void EnsureReassertLocked()
    {
        if (_suppressed)
        {
            return;
        }
        _reassertTimer ??= new Timer(
            _ => SafeReassert(),
            null,
            _ddcMode ? DdcReassertInterval : FallbackReassertInterval,
            _ddcMode ? DdcReassertInterval : FallbackReassertInterval);
    }

    private void SafeReassert()
    {
        lock (_sync)
        {
            if (!_active || _suppressed)
            {
                return;
            }
            TryTurnOffLocked();
        }
    }

    private bool IsStreamingLocked() => _streaming.IsStreamingActive();

    /// <summary>
    /// 取消保护后的恢复重试：MONITOR_ON 广播若落在 Sunshine 拓扑恢复窗口内会失效，
    /// 因此在 2s/6s/15s/30s 重试唤醒（保护重新开启或再次取消时，旧重试自动作废）。
    /// 等待期间若串流再次开始，转为推迟唤醒，绝不向串流中的显示器广播电源消息。
    /// 串流刚结束的窗口内，第一次重试附带“物理显示器重新挂载”兜底：
    /// 虚拟显示器会话结束后面板对广播唤醒无响应（本机实测），挂载事件是唯一可靠唤醒手段。
    /// </summary>
    private void ScheduleWakeRetriesLocked()
    {
        var generation = ++_wakeGeneration;

        // 完整重试链仅用于“串流刚结束”窗口：虚拟显示器会话结束后面板可能对广播
        // 唤醒无响应（本机实测），需要多次尝试 + 重挂载兜底。普通待机/隐私唤醒
        // 第一次广播即点亮，多余的重试广播会反复复位显示管线、鼠标微动干扰光标
        // （实测：解锁后桌面巨卡、光标移动困难的根因），故只保留一次轻量重试。
        var postStream = _lastStreamingSessionEndedAt is { } endedAt
            && DateTime.UtcNow - endedAt < ReattachWindow;
        var delays = postStream ? new[] { 2, 6, 15, 30 } : new[] { 2 };

        _ = Task.Run(async () =>
        {
            var reattachDone = false;
            for (var i = 0; i < delays.Length; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(delays[i]));

                var doReattach = false;
                lock (_sync)
                {
                    if (_active || generation != _wakeGeneration)
                    {
                        return;
                    }
                    if (IsStreamingLocked())
                    {
                        // 等待期间串流又开始了：推迟到串流结束后唤醒。
                        // 必须同时给出推迟起点：SafeWatchStreaming 的 20 秒强制点亮兜底是按
                        // _wakeDeferredAt 算过期的，留 null 会让兜底永不触发——串流检测一旦误判，
                        // 屏幕就再无机会点亮（违背 fail-open）。已有起点则保留，避免反复续期把兜顶掉。
                        _wakePending = true;
                        _wakeDeferredAt ??= DateTime.UtcNow;
                        StartStreamingWatchLocked();
                        return;
                    }
                    // 用户已经开始操作（屏幕必然已点亮）：取消剩余重试，避免骚扰。
                    // 死面板场景用户按键是零星的（间隔 > 1s），重试链会在间隔中继续推进。
                    if (_isUserActive())
                    {
                        Log("wake retries cancelled: user active");
                        return;
                    }
                    Log($"wake retry {i + 1}");
                    TryTurnOnLocked();

                    // 重挂载兜底放在“首个用户空闲的重试点”执行（不限于第一次重试）：
                    // 面板点亮时用户会立刻开始输入 → 空闲点不会出现 → 重挂载自然跳过（无闪断）；
                    // 面板真不响应时用户按键零星 → 空闲点出现 → 重挂载执行（唯一可靠点亮手段）。
                    if (!reattachDone
                        && _lastStreamingSessionEndedAt is { } sessionEnded
                        && DateTime.UtcNow - sessionEnded < ReattachWindow)
                    {
                        reattachDone = true;
                        doReattach = true;
                    }
                }

                if (doReattach)
                {
                    // 锁外执行（挂载耗时约 1 秒，含面板断电等待）。
                    Log("post-stream wake: re-attaching physical display");
                    var ok = DisplayReattacher.ReattachPhysicalDisplays(
                        () => _streaming.IsStreamingActive());
                    Log(ok ? "physical display re-attached" : "physical display re-attach FAILED");
                }
            }
        });
    }

    private void TryTurnOffLocked()
    {
        // 串流守卫（纵深防御）：检测到串流时绝不广播关屏——
        // 广播会复位 GPU 显示输出，破坏 Sunshine 帧捕获（串流端卡顿/光标闪烁）。
        // 输入即压黑改用物理输入信号后，串流启动检测延迟窗口内也不应到达这里，
        // 此守卫兜底。
        if (IsStreamingLocked())
        {
            return;
        }

        if (!_ddcMode && !_ddcProbed)
        {
            _ddcProbed = true;
            try
            {
                _ddcMode = DdcCiController.TurnOffAll();
            }
            catch
            {
                _ddcMode = false;
            }
        }

        if (_ddcMode)
        {
            try
            {
                // DDC 模式：背光不会被输入唤醒，周期重申幂等。
                DdcCiController.TurnOffAll();
                return;
            }
            catch
            {
                _ddcMode = false;
                // DDC 中途失效：回退广播关屏。
            }
        }
        Log("broadcast MONITOR_OFF");
        BroadcastMonitorPower(Win32Constants.MONITOR_OFF);
        // 重新进入熄灭：终止显示保持请求（若有），避免状态机冲突。
        Interlocked.Exchange(ref _displayRequiredUntil, 0);
    }

    private void TryTurnOnLocked()
    {
        if (IsStreamingLocked())
        {
            // 串流期间绝不广播电源消息（物理屏已被 Sunshine 停用，广播只会干扰帧捕获）。
            Log("wake skipped: streaming active");
            return;
        }
        WakeDisplayLocked();
    }

    /// <summary>无条件执行唤醒（调用方已确认可以唤醒，或走 fail-open 强制路径）。</summary>
    private void WakeDisplayLocked()
    {
        if (_ddcMode)
        {
            try
            {
                DdcCiController.TurnOnAll();
                _ddcMode = false;
            }
            catch
            {
                // DDC 恢复失败时兜底发送系统恢复消息。
            }
        }
        Log("broadcast MONITOR_ON");
        BroadcastMonitorPower(Win32Constants.MONITOR_ON);

        // 兜底 1：持续持有 ES_DISPLAY_REQUIRED（见 HoldDisplayRequiredLocked 注释）。
        HoldDisplayRequiredLocked();

        // 兜底 2：真实位移的鼠标微动唤醒 DPMS 显示器（1px 微动可能被合并忽略，加大位移）。
        NudgeMouseToWakeDisplay();
    }

    /// <summary>
    /// 强制唤醒（fail-open）：忽略“串流中”判定，立即点亮显示器并安排重试。
    /// 用于应用退出/启动救援等场景——这些时刻用户就在现场，屏幕必须亮起。
    /// 注意：不伪造“串流刚结束”标记——只有真实串流会话刚结束时才允许
    /// 重挂载兜底与完整重试链，否则启动救援会凭空触发重挂载和 6/15/30 秒
    /// 的多余广播（实测：用户在桌面工作时被反复广播干扰）。
    /// </summary>
    public void ForceWake()
    {
        lock (_sync)
        {
            _wakePending = false;
            _wakeDeferredAt = null;
            _suppressed = false;
            StopStreamingWatchLocked();
            Log("force wake");
            WakeDisplayLocked();
            ScheduleWakeRetriesLocked();
        }
    }

    /// <summary>
    /// 持续持有 ES_DISPLAY_REQUIRED（专用线程，SetThreadExecutionState 线程关联）。
    /// 为什么不是 300ms 脉冲：系统“显示器电源状态机”需要**持续的**请求或真实输入
    /// 才能从“已关闭”拉回“开启”。脉冲过期后状态机立即重新关屏——实测
    /// “紧急恢复后屏亮一下又迅速变黑”的根因（注入的鼠标微动不计入真实输入，
    /// 被钩子吞掉的紧急键同样不算）。
    /// 附带收益：持有时系统不合并定时器，解除“屏黑期间心跳/IPC 被拖慢数秒”
    /// （实测紧急命令 9 秒才送达服务端）的问题。
    /// 释放条件：用户开始真实操作（屏幕必然已亮）或 30 秒超时；重新关屏时也会终止。
    /// </summary>
    private void HoldDisplayRequiredLocked()
    {
        Interlocked.Exchange(ref _displayRequiredUntil, Environment.TickCount64 + 30_000);
        if (_displayHoldThread is { IsAlive: true })
        {
            return; // 持有线程已运行：只需续期。
        }
        _displayHoldThread = new Thread(HoldDisplayRequiredLoop)
        {
            IsBackground = true,
            Name = "Guardian.DisplayHold",
        };
        _displayHoldThread.Start();
    }

    private void HoldDisplayRequiredLoop()
    {
        try
        {
            while (Environment.TickCount64 < Interlocked.Read(ref _displayRequiredUntil))
            {
                if (_isUserActive())
                {
                    // 用户开始操作：真实输入已重置系统显示状态机，可安全释放。
                    break;
                }
                NativeMethods.SetThreadExecutionState(
                    Win32Constants.ES_CONTINUOUS | Win32Constants.ES_DISPLAY_REQUIRED);
                Thread.Sleep(400);
            }
        }
        catch
        {
            // 持有失败不影响主流程。
        }
        finally
        {
            NativeMethods.SetThreadExecutionState(Win32Constants.ES_CONTINUOUS);
            Log("display required hold released");
        }
    }

    private static void NudgeMouseToWakeDisplay()
    {
        try
        {
            NativeMethods.mouse_event(Win32Constants.MOUSEEVENTF_MOVE, 30, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            NativeMethods.mouse_event(Win32Constants.MOUSEEVENTF_MOVE, -30, 0, 0, UIntPtr.Zero);
        }
        catch
        {
            // 微动失败不影响主流程。
        }
    }

    private void Log(string message)
    {
        try
        {
            Diagnostics?.Invoke(message);
        }
        catch
        {
            // 诊断回调失败不影响主流程。
        }
    }

    /// <summary>
    /// 使用非阻塞投递（PostMessage 语义）：
    /// 同步 SendMessage 广播会因无响应窗口而挂起，已在真实环境验证必须规避。
    /// </summary>
    private static void BroadcastMonitorPower(int state)
    {
        NativeMethods.PostMessage(
            new IntPtr(-1), // HWND_BROADCAST
            Win32Constants.WM_SYSCOMMAND,
            (IntPtr)Win32Constants.SC_MONITORPOWER,
            (IntPtr)state);
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SetRestSession(false);
        Apply(false);
    }
}
