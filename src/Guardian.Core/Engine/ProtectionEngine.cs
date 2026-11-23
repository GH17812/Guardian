using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Core.Ports;

namespace Guardian.Core.Engine;

/// <summary>
/// 保护引擎：系统保护状态的唯一权威。
/// 设计要点：
/// 1. 所有状态变更在单锁内完成；恢复流程按 fail-open 原则执行且永不抛出。
/// 2. 适配器调用失败时立即触发恢复，并把状态置为 Failed（如实呈现，绝不假报成功）。
/// 3. UI 心跳、会话超时、电源状态变化由内部定时器驱动。
/// 4. 快照事件一律在锁外触发，避免事件处理器回调造成重入。
/// </summary>
public sealed class ProtectionEngine : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>电池电量低于该百分比时自动退出待机模式（仅电池供电时）。</summary>
    private const int RestModeBatteryLowPercent = 20;

    private readonly object _sync = new();
    private readonly IGuardianLog _log;
    private readonly IClock _clock;
    private readonly IDisplayPrivacyController _display;
    private readonly IPowerRequestController _power;
    private readonly IInputIsolationController _input;
    private readonly ISystemStateReader _system;
    private readonly IGuardianEventSink _sink;
    private readonly SessionManager _sessions = new();
    private readonly SettingsHolder _settings;
    private readonly RecoveryCoordinator _recovery;
    private readonly CapabilityState _capabilities = new();
    private readonly DateTimeOffset _engineStartedAt;
    private readonly Timer _tickTimer;

    private ProtectionMode _manualMode = ProtectionMode.Privacy;
    private bool _manualEnabled;
    private DateTimeOffset? _manualEnabledAt;
    private ProtectionState _state = ProtectionState.Unknown;
    private DesiredPolicy _appliedPolicy = DesiredPolicy.None;
    private string? _lastOperation;
    private DateTimeOffset? _lastOperationAt;
    private string? _lastOperationBy;
    private RecoveryReason? _lastRecoveryReason;
    private DateTimeOffset? _lastClientHeartbeat;
    private bool _clientLostHandled;
    private bool _powerSuspended;
    private bool _restModeActive;
    private bool _restSessionActive;
    private bool _restStreamingActive;

    /// <summary>客户端最近一次上报的串流状态（与是否武装无关，供开启待机时兜底判定）。</summary>
    private bool _streamingReported;
    private int _recovering;
    private bool _disposed;

    /// <summary>状态快照变化（在锁外触发）。</summary>
    public event Action<ProtectionSnapshot>? SnapshotChanged;

    public ProtectionEngine(
        IGuardianLog log,
        IClock clock,
        IDisplayPrivacyController display,
        IPowerRequestController power,
        IInputIsolationController input,
        ISystemStateReader system,
        IGuardianEventSink sink,
        GuardianSettings initialSettings)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _display = display ?? throw new ArgumentNullException(nameof(display));
        _power = power ?? throw new ArgumentNullException(nameof(power));
        _input = input ?? throw new ArgumentNullException(nameof(input));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _settings = new SettingsHolder(initialSettings);
        _recovery = new RecoveryCoordinator(display, power, input);
        _engineStartedAt = clock.UtcNow.ToLocalTime();
        _tickTimer = new Timer(_ => SafeTick(), null, TickInterval, TickInterval);
    }

    // ---------------------------------------------------------------- 命令

    /// <summary>在锁内执行命令，锁外触发快照通知。</summary>
    private EngineResult RunCommand(Func<EngineResult> action)
    {
        EngineResult result;
        lock (_sync)
        {
            result = action();
        }
        Notify();
        return result;
    }

    public EngineResult EnableProtection(ProtectionMode mode, string source, bool displayPrivacyCheckAcknowledged)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }
            if (mode is ProtectionMode.None)
            {
                return EngineResult.Fail(ErrorCodes.InvalidArgument, "mode 不能为 None");
            }
            if (mode is ProtectionMode.EnhancedIsolation)
            {
                return EngineResult.Fail(ErrorCodes.Unsupported, "增强隔离为第二阶段能力，当前版本未实现。");
            }
            if (mode >= ProtectionMode.InputIsolation)
            {
                if (!_input.IsSupported)
                {
                    return EngineResult.Fail(ErrorCodes.Unsupported, "当前环境不支持实验性输入隔离。");
                }
                if (!_settings.Get().AllowIsolationDuringRemoteSession && _system.IsRemoteSession())
                {
                    return EngineResult.Fail(ErrorCodes.InvalidState, "检测到远程会话（RDP）。远程输入可能被误拦截，已按安全策略拒绝启用输入隔离。可在设置中显式允许。");
                }
            }
            if (!displayPrivacyCheckAcknowledged && _capabilities.Get().DisplayPrivacyValidated != true)
            {
                return EngineResult.Fail(ErrorCodes.ConfirmationRequired, "display-privacy-check-required");
            }

            var now = _clock.UtcNow;
            _manualMode = mode;
            _manualEnabled = true;
            _manualEnabledAt = now;

            var policy = ComputePolicyLocked();
            if (!TryApplyLocked(policy, out var applyError))
            {
                return EngineResult.Fail(ErrorCodes.InternalError, applyError ?? "应用保护策略失败");
            }

            _lastOperation = $"enable-protection:{mode}";
            _lastOperationAt = now.ToLocalTime();
            _lastOperationBy = source;
            _log.Write(GuardianLogLevel.Information, "engine", $"保护已开启，模式 {mode}", $"source={source}");
            return EngineResult.Ok();
        });

    public EngineResult DisableProtection(string source)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }

            var now = _clock.UtcNow;
            _manualEnabled = false;
            _manualEnabledAt = null;

            var policy = ComputePolicyLocked();
            if (!TryApplyLocked(policy, out var applyError))
            {
                return EngineResult.Fail(ErrorCodes.InternalError, applyError ?? "应用保护策略失败");
            }

            _lastOperation = "disable-protection";
            _lastOperationAt = now.ToLocalTime();
            _lastOperationBy = source;
            _log.Write(GuardianLogLevel.Information, "engine", "手动保护已解除", $"source={source}");
            return EngineResult.Ok();
        });

    /// <summary>紧急恢复：无条件释放全部保护并结束全部会话。永不失败（结果体现在状态与恢复记录中）。</summary>
    public EngineResult Recover(RecoveryReason reason, RecoverySource source, string? detail = null)
    {
        if (Interlocked.Exchange(ref _recovering, 1) == 1)
        {
            // 已有恢复流程在进行，本次请求合并，不再重复执行。
            return EngineResult.Ok();
        }

        try
        {
            lock (_sync)
            {
                var now = _clock.UtcNow;
                _state = ProtectionState.Recovering;
                _log.Write(GuardianLogLevel.Warning, "engine", "开始恢复流程", $"reason={reason}, source={source}, detail={detail}");

                var record = _recovery.Execute(reason, source, detail, _clock);
                _sessions.EndAll(MapSessionEndReason(reason), now);
                _manualEnabled = false;
                _manualEnabledAt = null;
                _powerSuspended = false;
                _appliedPolicy = DesiredPolicy.None;
                // 注意：待机模式不随恢复解除——按产品规则“只有用户手动关闭才关闭待机”，
                // 紧急恢复只结束待机会话（由客户端处理唤醒+锁屏），武装状态保持；
                // 低电量自动退出是唯一非手动的解除通道。

                // 如实呈现：恢复步骤全部成功才算 Inactive，否则标记 Failed（未知/异常）。
                _state = record.Success ? ProtectionState.Inactive : ProtectionState.Failed;
                _lastOperation = "recover";
                _lastRecoveryReason = reason;
                _lastOperationAt = now.ToLocalTime();
                _lastOperationBy = source == RecoverySource.Local ? "local" : "remote";

                _sink.OnRecovery(record);
                _log.Write(record.Success ? GuardianLogLevel.Information : GuardianLogLevel.Critical,
                    "engine",
                    record.Success ? "恢复完成" : "恢复部分失败，系统状态可能未知",
                    record.Success ? null : string.Join("; ", record.Steps.Where(s => !s.Success).Select(s => $"{s.Step}:{s.Error}")));
            }
            Notify();
            return EngineResult.Ok();
        }
        finally
        {
            Interlocked.Exchange(ref _recovering, 0);
        }
    }

    public EngineResult CreateSession(CreateSessionPayload payload)
        => RunCommand(() =>
        {
            if (string.IsNullOrWhiteSpace(payload.Name))
            {
                return EngineResult.Fail(ErrorCodes.InvalidArgument, "会话名称不能为空");
            }
            if (payload.RequiredMode is ProtectionMode.EnhancedIsolation)
            {
                return EngineResult.Fail(ErrorCodes.Unsupported, "增强隔离为第二阶段能力，当前版本未实现。");
            }
            if (payload.RequiredMode is ProtectionMode.None)
            {
                return EngineResult.Fail(ErrorCodes.InvalidArgument, "会话必须声明所需的保护模式");
            }

            var now = _clock.UtcNow;
            var info = new SessionInfo
            {
                Id = Guid.NewGuid(),
                Name = payload.Name.Trim(),
                WorkloadType = payload.WorkloadType,
                Status = SessionStatus.Active,
                RequiredMode = payload.RequiredMode,
                Source = payload.Source,
                AllowAutoEnd = payload.AllowAutoEnd,
                CreatedAt = now.ToLocalTime(),
                MaxDurationMinutes = payload.MaxDurationMinutes is > 0 ? payload.MaxDurationMinutes : null,
                SourceDetail = payload.SourceDetail,
            };

            var session = _sessions.Create(info);
            _sink.OnSessionChanged(info);
            _log.Write(GuardianLogLevel.Information, "sessions", $"会话已创建：{info.Name}", $"id={info.Id}, type={info.WorkloadType}, mode={info.RequiredMode}");

            var policy = ComputePolicyLocked();
            if (!TryApplyLocked(policy, out var applyError))
            {
                return EngineResult.Fail(ErrorCodes.InternalError, applyError ?? "应用保护策略失败");
            }

            _lastOperation = $"create-session:{info.Name}";
            _lastOperationAt = now.ToLocalTime();
            _lastOperationBy = info.SourceDetail ?? payload.Source.ToString();
            return EngineResult.Ok();
        });

    public EngineResult EndSession(Guid sessionId, SessionEndReason reason)
        => RunCommand(() =>
        {
            if (!_sessions.End(sessionId, reason, _clock.UtcNow, out var session))
            {
                return EngineResult.Fail(ErrorCodes.InvalidArgument, "会话不存在或已结束");
            }

            _sink.OnSessionChanged(session!.Info);
            _log.Write(GuardianLogLevel.Information, "sessions", $"会话已结束：{session.Info.Name}", $"reason={reason}");

            var policy = ComputePolicyLocked();
            if (!TryApplyLocked(policy, out var applyError))
            {
                return EngineResult.Fail(ErrorCodes.InternalError, applyError ?? "应用保护策略失败");
            }

            _lastOperation = $"end-session:{session.Info.Name}";
            _lastOperationAt = _clock.UtcNow.ToLocalTime();
            return EngineResult.Ok();
        });

    public List<SessionInfo> ListSessions()
    {
        lock (_sync)
        {
            return _sessions.GetAll().Select(s => s.Info).ToList();
        }
    }

    public GuardianSettings GetSettings()
        => _settings.Get();

    public EngineResult UpdateSettings(GuardianSettings incoming)
        => RunCommand(() =>
        {
            _settings.Update(incoming);
            _sink.OnSettingsChanged(_settings.Get());
            _log.Write(GuardianLogLevel.Information, "settings", "设置已更新", $"uiLossAction={incoming.UiLossAction}, resumePolicy={incoming.ResumePolicy}");

            // 设置可能影响当前策略（例如 OnlyOnAcPower），立即重估。
            CheckAcPowerLocked();
            return EngineResult.Ok();
        });

    public CapabilityReport GetCapabilities()
        => _capabilities.Get();

    public EngineResult ConfirmDisplayPrivacyCheck(ConfirmDisplayPrivacyCheckPayload payload)
        => RunCommand(() =>
        {
            var report = _capabilities.SetDisplayPrivacyValidation(payload.RemoteCaptureOk, payload.Note, _clock.UtcNow.ToLocalTime());
            _sink.OnCapabilityChanged(report);
            _log.Write(payload.RemoteCaptureOk ? GuardianLogLevel.Information : GuardianLogLevel.Warning,
                "capabilities",
                payload.RemoteCaptureOk ? "显示隐私验证通过：关闭显示器后远程捕获正常" : "显示隐私验证失败：关闭显示器后远程捕获异常",
                payload.Note);
            return EngineResult.Ok();
        });

    /// <summary>合并客户端上报的输入设备清单（Session 0 服务无法枚举交互会话设备）。</summary>
    public EngineResult ReportInputDevices(List<InputDeviceCapability> devices)
        => RunCommand(() =>
        {
            if (devices is null || devices.Count == 0)
            {
                return EngineResult.Fail(ErrorCodes.InvalidArgument, "设备清单为空");
            }
            var report = _capabilities.MergeInputDevices(devices, _clock.UtcNow.ToLocalTime());
            _sink.OnCapabilityChanged(report);
            _log.Write(GuardianLogLevel.Information, "capabilities",
                $"客户端上报输入设备：{string.Join(", ", devices.Select(d => $"{d.DeviceType}:{d.Count}"))}");
            return EngineResult.Ok();
        });

    // ---------------------------------------------------------------- 待机模式（PS5 式）

    /// <summary>
    /// 开启待机模式：持有离开模式（Away Mode）请求——此后合盖/电源键/睡眠动作
    /// 不再进入 S3，而是“屏幕熄灭、系统保持运行、网络在线”，串流随时可达。
    /// 状态持久化（RestModeArmed），服务重启后自动恢复。
    /// </summary>
    public EngineResult EnableRestMode(string source, bool enterSession = true)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }
            if (_restModeActive)
            {
                return EngineResult.Ok();
            }

            _restModeActive = true;

            // 服务开机恢复只恢复"武装"，不能连带把待机会话置为进行中：
            // 那样从开机起就持有离开模式，而本机实测持续持有会让桌面无声
            // （原本要等 Guardian.App 上线上报会话结束才释放，App 不开就一直闷着）。
            // 用户主动勾选仍然立即进入待机，那是既有产品规则。
            _restSessionActive = enterSession;

            // 开启待机的瞬间就得知道有没有在串流：未武装时客户端根本不轮询上报，
            // SetStreamingActive 又被 !_restModeActive 的闸门挡住，直接 Recompute 会在串流中
            // 打开离开模式——离开模式静音音频端点，Sunshine 音频流会建立失败。
            // 取最近一次上报值兜底；客户端武装后 1 秒内会重报真值自动纠偏。
            _restStreamingActive = _streamingReported;
            RecomputeAwayModeLocked();
            PersistRestModeLocked(true);
            _lastOperation = "enable-rest-mode";
            _lastOperationAt = _clock.UtcNow.ToLocalTime();
            _lastOperationBy = source;
            _log.Write(GuardianLogLevel.Information, "engine", "待机模式已开启（睡眠转为离开模式）", $"source={source}");
            return EngineResult.Ok();
        });

    /// <summary>关闭待机模式：释放离开模式请求，恢复正常睡眠行为。</summary>
    public EngineResult DisableRestMode(string source)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }
            if (!_restModeActive)
            {
                return EngineResult.Ok();
            }

            _restModeActive = false;
            _restSessionActive = false;
            _restStreamingActive = false;
            _power.SetAwayMode(false);
            PersistRestModeLocked(false);
            _lastOperation = "disable-rest-mode";
            _lastOperationAt = _clock.UtcNow.ToLocalTime();
            _lastOperationBy = source;
            _log.Write(GuardianLogLevel.Information, "engine", "待机模式已关闭", $"source={source}");
            return EngineResult.Ok();
        });

    /// <summary>
    /// 客户端上报待机会话状态。用户唤醒进桌面后会话结束：释放 Away Mode
    /// （Windows 离开模式会静音音频端点，持续持有会导致桌面无声、Sunshine 音频流失败），
    /// 但武装状态保持——下次睡眠仍进入待机（产品规则：只有手动关闭才退出待机）。
    /// </summary>
    public EngineResult SetRestSessionActive(bool active, string source)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }
            if (!_restModeActive || _restSessionActive == active)
            {
                return EngineResult.Ok();
            }

            _restSessionActive = active;
            RecomputeAwayModeLocked();
            _log.Write(GuardianLogLevel.Information, "engine",
                active ? "待机会话开始（离开模式）" : "待机会话结束（释放离开模式，音频恢复）",
                $"source={source}");
            return EngineResult.Ok();
        });

    /// <summary>
    /// 客户端上报串流状态。串流期间必须暂停 Away Mode：离开模式会静音音频端点，
    /// Sunshine 抓不到音频会导致整条流建立失败（audio stream establishment failed）。
    /// 屏幕熄灭此时由 Sunshine 虚拟显示器方案与客户端 DPMS 双重保障。
    /// </summary>
    public EngineResult SetStreamingActive(bool active, string source)
        => RunCommand(() =>
        {
            if (_disposed)
            {
                return EngineResult.Fail(ErrorCodes.ServiceUnavailable, "引擎已关闭");
            }
            // 未武装时也要记住上报值：武装期间客户端才轮询上报，不记就会在串流中开启离开模式
            // （离开模式静音音频端点，实测导致 Sunshine 音频流建立失败）。
            _streamingReported = active;
            if (!_restModeActive || _restStreamingActive == active)
            {
                return EngineResult.Ok();
            }

            _restStreamingActive = active;
            RecomputeAwayModeLocked();
            _log.Write(GuardianLogLevel.Information, "engine",
                active ? "串流开始：暂停离开模式（恢复音频捕获）" : "串流结束：恢复离开模式",
                $"source={source}");
            return EngineResult.Ok();
        });

    /// <summary>Away Mode 实际状态 = 武装 && 会话中 && 非串流。</summary>
    private void RecomputeAwayModeLocked()
        => _power.SetAwayMode(_restModeActive && _restSessionActive && !_restStreamingActive);

    /// <summary>待机模式武装状态写入设置（持久化，服务重启后恢复）。</summary>
    private void PersistRestModeLocked(bool armed)
    {
        var updated = _settings.Get().Clone();
        updated.RestModeArmed = armed;
        _settings.Update(updated);
        // 通知事件汇（服务端据此持久化到存储）。
        _sink.OnSettingsChanged(_settings.Get());
    }

    // ---------------------------------------------------------------- 服务端初始化 / 生命周期

    public void InitializeCapabilities(CapabilityReport deviceReport)
    {
        lock (_sync)
        {
            _capabilities.Initialize(deviceReport);
            _sink.OnCapabilityChanged(_capabilities.Get());
        }
        Notify();
    }

    public void LoadPersistedSessions(IEnumerable<SessionInfo> infos)
    {
        lock (_sync)
        {
            _sessions.Load(infos);
        }
    }

    /// <summary>服务启动时的 fail-open 恢复：无条件释放所有保护资源。</summary>
    public void StartupRestore()
    {
        lock (_sync)
        {
            var record = _recovery.Execute(RecoveryReason.StartupRestore, RecoverySource.Local, "service-startup", _clock);
            _state = record.Success ? ProtectionState.Inactive : ProtectionState.Failed;
            _appliedPolicy = DesiredPolicy.None;
            _lastOperation = "startup-restore";
            _lastOperationAt = _clock.UtcNow.ToLocalTime();
            _lastOperationBy = "service";
            _sink.OnRecovery(record);
            _log.Write(GuardianLogLevel.Information, "engine", "启动恢复完成：已确保系统处于无保护状态");
        }
        Notify();
    }

    /// <summary>按持久化会话恢复保护（ResumePolicy.ResumeLast）。</summary>
    public void ResumeAfterRestart()
    {
        lock (_sync)
        {
            var active = _sessions.GetActive();
            if (active.Count == 0)
            {
                _state = ProtectionState.Inactive;
                _lastOperation = "service-restart:no-active-sessions";
                _lastOperationAt = _clock.UtcNow.ToLocalTime();
            }
            else
            {
                var policy = PolicyAggregator.Aggregate(ProtectionMode.None, false, active);
                if (TryApplyLocked(policy, out var error))
                {
                    _state = ProtectionState.Active;
                    _lastOperation = "service-restart:resumed";
                    _log.Write(GuardianLogLevel.Information, "engine", $"服务重启后已恢复保护（{active.Count} 个会话）");
                }
                else
                {
                    _state = ProtectionState.Failed;
                    _log.Write(GuardianLogLevel.Critical, "engine", $"服务重启后恢复保护失败：{error}");
                }
            }
        }
        Notify();
    }

    // ---------------------------------------------------------------- 心跳与定时

    public void OnClientHeartbeat()
    {
        lock (_sync)
        {
            _lastClientHeartbeat = _clock.UtcNow;
            // 心跳恢复：重新武装“客户端丢失”检测，下一次丢失可再次触发恢复。
            _clientLostHandled = false;
        }
    }

    /// <summary>紧急恢复快捷键回调（来自输入隔离钩子线程，必须快速返回）。</summary>
    public void RequestEmergencyRecovery()
    {
        _log.Write(GuardianLogLevel.Warning, "engine", "检测到紧急恢复快捷键");
        Task.Run(() => Recover(RecoveryReason.EmergencyHotkey, RecoverySource.Local, "emergency-hotkey"));
    }

    public void Shutdown()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _tickTimer.Dispose();
        Recover(RecoveryReason.ServiceStop, RecoverySource.Local, "service-stop");
        _log.Write(GuardianLogLevel.Information, "engine", "引擎已关闭");
    }

    // ---------------------------------------------------------------- 快照

    public ProtectionSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            var power = SafeReadPower();
            var active = _sessions.GetActive();
            var effectiveMode = _state is ProtectionState.Active or ProtectionState.Activating
                ? _appliedPolicy.Mode
                : ProtectionMode.None;

            return new ProtectionSnapshot
            {
                State = _state,
                StateStampMs = Environment.TickCount64,
                ActiveMode = effectiveMode,
                ManualProtectionEnabled = _manualEnabled,
                DisplayPrivacyActive = SafeIsActive(_display),
                InputIsolationActive = SafeIsActive(_input),
                PowerRequestActive = SafeIsActive(_power),
                PowerRequestSuspended = _powerSuspended,
                ActiveSessionCount = active.Count,
                RestModeActive = _restModeActive,
                LastOperation = _lastOperation,
                LastOperationAt = _lastOperationAt,
                LastOperationBy = _lastOperationBy,
                LastRecoveryReason = _lastOperation == "recover" ? _lastRecoveryReason : null,
                ServerTime = _clock.UtcNow.ToLocalTime(),
                ServiceStartedAt = _engineStartedAt,
                ServiceVersion = ProductInfo.Version,
                RemoteSessionActive = SafeIsRemoteSession(),
                AcOnline = power.AcOnline,
                BatteryPercent = power.BatteryPercent,
                MonitorCount = power.MonitorCount,
                SystemBootAt = SafeGetBootTime(),
            };
        }
    }

    // ---------------------------------------------------------------- 内部实现

    private DesiredPolicy ComputePolicyLocked()
        => PolicyAggregator.Aggregate(_manualEnabled ? _manualMode : ProtectionMode.None, _manualEnabled, _sessions.GetActive());

    /// <summary>应用策略；适配器异常时触发 fail-open 恢复并如实置为 Failed。</summary>
    private bool TryApplyLocked(DesiredPolicy policy, out string? error)
    {
        error = null;
        try
        {
            if (policy.SystemRequired)
            {
                _power.Apply(!policy.DisplayPrivacy);
            }
            else
            {
                _power.Release();
                _powerSuspended = false;
            }

            if (policy.InputIsolation)
            {
                _input.Enable(RequestEmergencyRecovery, _settings.Get().EmergencyHotkeyKey);
            }
            else
            {
                _input.Disable();
            }

            if (policy.DisplayPrivacy)
            {
                _display.Enable();
            }
            else
            {
                _display.Disable();
            }

            _appliedPolicy = policy;
            _state = policy.SystemRequired ? ProtectionState.Active : ProtectionState.Inactive;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.Write(GuardianLogLevel.Critical, "engine", "应用保护策略失败，触发 fail-open 恢复", ex.ToString());
            try
            {
                var record = _recovery.Execute(RecoveryReason.AdapterFailure, RecoverySource.Local, ex.Message, _clock);
                _sink.OnRecovery(record);
                _sessions.EndAll(SessionEndReason.EmergencyRecovery, _clock.UtcNow);
                _manualEnabled = false;
                _manualEnabledAt = null;
                _appliedPolicy = DesiredPolicy.None;
                _state = record.Success ? ProtectionState.Inactive : ProtectionState.Failed;
            }
            catch (Exception recoverEx)
            {
                _log.Write(GuardianLogLevel.Critical, "engine", "恢复流程本身也失败", recoverEx.ToString());
                _state = ProtectionState.Failed;
            }
            return false;
        }
    }

    private void SafeTick()
    {
        try
        {
            Tick();
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Error, "engine", "定时任务异常", ex.ToString());
        }
    }

    internal void Tick()
    {
        bool clientLost = false;

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            var now = _clock.UtcNow;
            var changed = false;

            // 1. 会话超时
            var expired = _sessions.ExpireDue(now);
            foreach (var s in expired)
            {
                _sink.OnSessionChanged(s.Info);
                _log.Write(GuardianLogLevel.Information, "sessions", $"会话超时自动结束：{s.Info.Name}");
                _lastOperation = $"session-timeout:{s.Info.Name}";
                _lastOperationAt = now.ToLocalTime();
                changed = true;
            }

            // 2. 手动保护最长时长
            if (_manualEnabled
                && _settings.Get().MaxManualProtectionMinutes > 0
                && _manualEnabledAt is { } enabledAt
                && now >= enabledAt.AddMinutes(_settings.Get().MaxManualProtectionMinutes))
            {
                _manualEnabled = false;
                _manualEnabledAt = null;
                _log.Write(GuardianLogLevel.Information, "engine", "手动保护达到最长时长，已自动解除");
                _lastOperation = "manual-protection-timeout";
                _lastOperationAt = now.ToLocalTime();
                changed = true;
            }

            // 3. 客户端心跳（仅当曾经连接过客户端才判定丢失）。
            //    同一丢失事件只恢复一次（_clientLostHandled），心跳恢复后重新武装，
            //    避免客户端退出后每个 tick 重复执行恢复、刷爆日志。
            if (_lastClientHeartbeat is { } lastSeen
                && !_clientLostHandled
                && _settings.Get().UiLossAction == UiLossAction.Recover)
            {
                var threshold = TimeSpan.FromSeconds(_settings.Get().ClientLostThresholdSeconds);
                if (now - lastSeen > threshold)
                {
                    // 仅在确实有保护资源需要释放时才执行恢复；无保护时无需动作。
                    if (_manualEnabled || _appliedPolicy.SystemRequired || _sessions.GetActive().Count > 0)
                    {
                        clientLost = true;
                        _clientLostHandled = true;
                    }
                }
            }

            // 4. 待机模式低电量自动退出（fail-open：避免电量耗尽导致无法唤醒）。
            if (_restModeActive)
            {
                var power = SafeReadPower();
                if (!power.AcOnline && power.BatteryPercent is { } percent && percent < RestModeBatteryLowPercent)
                {
                    _restModeActive = false;
                    _restSessionActive = false;
                    _restStreamingActive = false;
                    _power.SetAwayMode(false);
                    PersistRestModeLocked(false);
                    _lastOperation = "rest-mode-battery-low";
                    _lastOperationAt = now.ToLocalTime();
                    _lastOperationBy = "battery-low";
                    _log.Write(GuardianLogLevel.Warning, "engine", "待机模式已因低电量自动退出", $"battery={percent}%");
                    changed = true;
                }
            }

            // 5. 交流电源检测
            changed |= CheckAcPowerLocked();

            if (changed)
            {
                var policy = ComputePolicyLocked();
                if (policy != _appliedPolicy)
                {
                    TryApplyLocked(policy, out _);
                }
            }
        }

        if (clientLost)
        {
            _log.Write(GuardianLogLevel.Warning, "engine", "客户端心跳超时，执行自动恢复");
            Recover(RecoveryReason.ClientLost, RecoverySource.Local, "client-lost");
        }

        Notify();
    }

    /// <summary>返回 true 表示策略相关状态发生了变化。</summary>
    private bool CheckAcPowerLocked()
    {
        if (!_settings.Get().OnlyOnAcPower)
        {
            return false;
        }

        var reading = SafeReadPower();
        if (!reading.AcOnline && _power.IsActive && !_powerSuspended)
        {
            _power.Release();
            _powerSuspended = true;
            _log.Write(GuardianLogLevel.Warning, "engine", "检测到电池供电，电源保护请求已挂起（仅交流电策略）");
            return true;
        }

        if (reading.AcOnline && _powerSuspended)
        {
            _powerSuspended = false;
            if (_appliedPolicy.SystemRequired)
            {
                _power.Apply(!_appliedPolicy.DisplayPrivacy);
            }
            _log.Write(GuardianLogLevel.Information, "engine", "恢复交流供电，电源保护请求已恢复");
            return true;
        }

        return false;
    }

    private static SessionEndReason MapSessionEndReason(RecoveryReason reason) => reason switch
    {
        RecoveryReason.EmergencyHotkey or RecoveryReason.AdapterFailure => SessionEndReason.EmergencyRecovery,
        RecoveryReason.ClientLost => SessionEndReason.ClientLost,
        RecoveryReason.SessionTimeout => SessionEndReason.Timeout,
        RecoveryReason.ServiceStop => SessionEndReason.ServiceStop,
        RecoveryReason.StartupRestore => SessionEndReason.ServiceRestart,
        _ => SessionEndReason.Other,
    };

    private SystemPowerReading SafeReadPower()
    {
        try
        {
            return _system.ReadPower();
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Error, "engine", "读取电源状态失败", ex.Message);
            return new SystemPowerReading(false, null, 0);
        }
    }

    private bool SafeIsRemoteSession()
    {
        try
        {
            return _system.IsRemoteSession();
        }
        catch
        {
            return false;
        }
    }

    private DateTimeOffset SafeGetBootTime()
    {
        try
        {
            return _system.GetSystemBootTime();
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static bool SafeIsActive(IDisplayPrivacyController c)
    {
        try { return c.IsActive; }
        catch { return false; }
    }

    private static bool SafeIsActive(IPowerRequestController c)
    {
        try { return c.IsActive; }
        catch { return false; }
    }

    private static bool SafeIsActive(IInputIsolationController c)
    {
        try { return c.IsActive; }
        catch { return false; }
    }

    private void Notify()
    {
        ProtectionSnapshot snapshot;
        try
        {
            snapshot = GetSnapshot();
        }
        catch
        {
            return;
        }
        try
        {
            SnapshotChanged?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Error, "engine", "快照通知处理器异常", ex.Message);
        }
    }

    public void Dispose()
    {
        Shutdown();
    }
}
