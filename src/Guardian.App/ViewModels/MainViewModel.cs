using System.Collections.ObjectModel;
using System.IO;
using System.ServiceProcess;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Guardian.App.Services;
using Guardian.App.Views;
using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Infrastructure.ServiceControl;
using Guardian.Windows.Display;
using Guardian.Windows.Input;
using Guardian.Windows.Networking;
using Guardian.Windows.Power;

namespace Guardian.App.ViewModels;

/// <summary>主界面视图模型：驱动连接状态、保护状态、模式选择、磁贴与工作会话。</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly GuardianClientService _client;
    private readonly AppSettingsService _appSettings;
    private readonly LocalizationManager _localization;
    private readonly ThemeManager _theme;

    /// <summary>显示隐私执行器：服务端（Session 0）广播不可靠，由客户端在交互会话执行关屏/恢复。</summary>
    private readonly DisplayPrivacyClient _displayExecutor = new();
    private readonly CursorBoundaryClip _cursorClip = new();

    /// <summary>
    /// 输入隔离执行器：低层钩子必须安装在交互会话（服务端 Session 0 钩子监听不到交互会话输入），
    /// 由客户端按服务端快照执行；服务端仍是状态权威。紧急组合键在钩子内部检测并回调。
    /// </summary>
    private readonly InputIsolationController _inputIsolation = new();

    /// <summary>串流会话检测（UI 提示用）：屏幕被串流端接管时如实告知用户，避免“黑屏却显示未启动”的误解。</summary>
    private readonly StreamingSessionDetector _streaming = new();

    /// <summary>
    /// 待机模式客户端状态机：待机会话（屏幕熄灭）+ 本地输入唤醒（锁屏凭证）。
    /// 优先级：保护 > 待机；串流 > 待机（详见 RestModeController）。
    /// </summary>
    private readonly RestModeController _restMode;

    private ProtectionSnapshot? _snapshot;
    private CapabilityReport? _capabilities;
    private GuardianSettings? _serverSettings;
    private bool _devicesReported;
    private bool _displaySuppressed;
    private bool _showStreamingNote;
    private bool _lastProtectionActive;
    private bool _lastRestSessionActive;
    private bool _startupDisplayRescueDone;
    private bool _restModeSyncDone;

    /// <summary>
    /// 紧急热键后的本地覆盖：服务端恢复命令完成前，心跳可能送回旧快照（保护仍=开启），
    /// 导致客户端把刚恢复的屏幕重新关掉（“一闪恢复→又黑屏→闪回”的根因）。
    /// 覆盖期间忽略旧快照的显示/输入保护标志；3 秒后自动失效（心跳 1 秒，过期快照不可能存活更久）。
    /// </summary>
    private volatile bool _emergencyLocalOverride;

    // 已应用快照的生成时刻（机器级单调 TickCount64）：小于它的快照即过期，直接丢弃。
    // 只在一次连续连接内有意义：断连后归零重来，避免机器重启使基数回拨时被误判成“全部过期”。
    private long _lastSnapshotStampMs;

    private bool _busy;
    private bool _isPrivacy = true;
    private bool _isInputIsolation;
    private bool _isEnhanced;

    private ConnectionStatus _connectionStatus = ConnectionStatus.Disconnected;
    private ProtectionState _currentState = ProtectionState.Unknown;
    private ProtectionMode _activeMode = ProtectionMode.None;
    private ProtectionMode _selectedMode = ProtectionMode.Privacy;

    private string _serviceVersionText = string.Empty;
    private string _stateText = string.Empty;
    private Brush _stateBrush = Brushes.Gray;
    private string _activeModeText = string.Empty;
    private string _lastOperationText = string.Empty;
    private bool _isProtectionActive;
    private string _toggleButtonText = string.Empty;

    private string _connectionText = string.Empty;
    private Brush _connectionBrush = Brushes.Gray;
    private bool _showServiceBanner;
    private string _bannerText = string.Empty;

    private int _lastSessionCount = -1;

    private readonly ObservableCollection<SessionItemViewModel> _sessions = new();
    private readonly ObservableCollection<StatusTileViewModel> _tiles = new();

    public MainViewModel(
        GuardianClientService client,
        AppSettingsService appSettings,
        LocalizationManager localization,
        ThemeManager theme)
    {
        _client = client;
        _appSettings = appSettings;
        _localization = localization;
        _theme = theme;

        ToggleProtectionCommand = new RelayCommand(_ => { _ = ToggleProtectionAsync(); });
        EnablePrivacyCommand = new RelayCommand(_ => { _ = EnablePrivacyFromTrayAsync(); });
        DisableProtectionCommand = new RelayCommand(_ => { _ = DisableProtectionAsync(); });
        EmergencyRecoverCommand = new RelayCommand(_ => { _ = EmergencyRecoverAsync(); });
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());
        OpenLogsCommand = new RelayCommand(_ => { _ = OpenLogsAsync(); });
        CreateSessionCommand = new RelayCommand(_ => { _ = CreateSessionAsync(); });
        EndSessionCommand = new RelayCommand(p => { _ = EndSessionAsync(p as SessionItemViewModel); });
        InstallStartServiceCommand = new RelayCommand(_ => { _ = InstallOrStartServiceAsync(); });

        _theme.ThemeChanged += (_, _) => RefreshDynamicContent();
        _localization.LanguageChanged += (_, _) => RefreshDynamicContent();
        _displayExecutor.SuppressionChanged += OnDisplaySuppressionChanged;
        _displayExecutor.Diagnostics += msg => App.DiagLog($"[display] {msg}");
        _cursorClip.Diagnostics += msg => App.DiagLog($"[cursor] {msg}");
        // 隔离钩子的紧急键判定证据（COMBO HIT / miss + 修饰键读数）：
        // 此前该钩子完全无声，导致“按了没反应”无法定位是主键、修饰键还是通道问题。
        _inputIsolation.Diagnostics += msg => App.DiagLog(msg);

        _restMode = new RestModeController(
            setDisplayRest: rest => _displayExecutor.SetRestSession(rest),
            onEmergency: EmergencyRecoverFromHotkey,
            onSessionStateChanged: active => _ = ReportToServiceSafe(
                () => _client.SetRestSessionAsync(active, "rest-session")),
            onStreamingStateChanged: active => _ = ReportToServiceSafe(
                () => _client.SetStreamingAsync(active, "streaming")));

        // 串流环境：客户端只负责"看见屏幕"和"夺回物理屏"，装驱动/改配置/重启服务都由服务端以 SYSTEM 执行。
        _streamingEnv = new StreamingEnvWatcher(_client, RestorePhysicalDisplayAsync, AttachVirtualDisplayAsync);
        _streamingEnv.ProfileChanged += profile =>
        {
            // 一键过程要跑几分钟、跨进程、还会被互锁拦停；没有这行取证日志，事后只能猜界面当时显示了什么。
            App.DiagLog($"[streaming-env] state={profile.State} sunshine={profile.Sunshine.Version}"
                + $" file={profile.Sunshine.Configured} effective={profile.Sunshine.EffectiveConfigured}"
                + $" device={profile.VirtualDisplay.DevicePresent}/{profile.VirtualDisplay.DeviceEnabled}/{profile.VirtualDisplay.Enumerated}"
                + $" physical={profile.VirtualDisplay.PhysicalDisplayActive?.ToString() ?? "unknown"}"
                + $" guid={profile.VirtualDisplay.OutputGuid ?? "-"} pending={profile.PhysicalRestorePending}");
            StreamingEnvUiThread(() => ApplyStreamingEnvProfile(profile));
        };
        _streamingEnv.Diagnostics += text =>
        {
            // 同步落 app-debug.log：一键过程要跑几分钟，只靠界面上一行文字追不回时序。
            App.DiagLog($"[streaming-env] {text}");
            StreamingEnvUiThread(() =>
            {
                _streamingEnvActivity = text;
                OnPropertyChanged(nameof(StreamingEnvActivityText));
            });
        };

        // 用户活动探针：唤醒重试/重挂载在用户开始操作后立即取消（避免桌面卡顿）。
        _displayExecutor.SetUserActivityProbe(() => _restMode.IsUserActive);
        _restMode.StateChanged += () =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                RefreshRestModeVisuals();
            }
            else
            {
                dispatcher.BeginInvoke(RefreshRestModeVisuals);
            }
        };

        InitializeTiles();
        RefreshDynamicContent();
    }

    /// <summary>紧急热键主键变更（服务端设置加载后触发），参数为虚拟键码。</summary>
    public event Action<int>? EmergencyHotkeyKeyChanged;

    // ---------- 命令 ----------

    public RelayCommand ToggleProtectionCommand { get; }
    public RelayCommand EnablePrivacyCommand { get; }
    public RelayCommand DisableProtectionCommand { get; }
    public RelayCommand EmergencyRecoverCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CreateSessionCommand { get; }
    public RelayCommand EndSessionCommand { get; }
    public RelayCommand InstallStartServiceCommand { get; }

    // ---------- 集合 ----------

    public ObservableCollection<SessionItemViewModel> Sessions => _sessions;
    public ObservableCollection<StatusTileViewModel> Tiles => _tiles;

    // ---------- 连接 / 服务状态 ----------

    public ConnectionStatus ConnectionStatus
    {
        get => _connectionStatus;
        private set
        {
            if (SetProperty(ref _connectionStatus, value))
            {
                RefreshConnectionVisuals();
                OnPropertyChanged(nameof(CanOperate));
            }
        }
    }

    public string ConnectionText
    {
        get => _connectionText;
        private set => SetProperty(ref _connectionText, value);
    }

    public Brush ConnectionBrush
    {
        get => _connectionBrush;
        private set => SetProperty(ref _connectionBrush, value);
    }

    public string ServiceVersionText
    {
        get => _serviceVersionText;
        private set => SetProperty(ref _serviceVersionText, value);
    }

    public bool ShowServiceBanner
    {
        get => _showServiceBanner;
        private set => SetProperty(ref _showServiceBanner, value);
    }

    public string BannerText
    {
        get => _bannerText;
        private set => SetProperty(ref _bannerText, value);
    }

    /// <summary>串流会话提示：串流期间屏幕由串流端（虚拟显示器）接管，屏幕熄灭不代表 Guardian 保护已开启。</summary>
    public bool ShowStreamingNote
    {
        get => _showStreamingNote;
        private set
        {
            if (SetProperty(ref _showStreamingNote, value))
            {
                App.DiagLog($"streaming note: {(value ? "shown" : "hidden")}");
            }
        }
    }

    public string StreamingNoteText => L("Str.StreamingNote");

    // ---------- 待机模式（PS5 式） ----------

    /// <summary>待机模式开关（双向绑定，false→true 触发开启命令，true→false 触发关闭命令）。</summary>
    public bool IsRestModeArmed
    {
        get => _restMode.IsArmed;
        set
        {
            if (value == _restMode.IsArmed)
            {
                return;
            }
            _ = SetRestModeAsync(value);
        }
    }

    /// <summary>待机模式状态文案：待机中 / 已启用 / 关闭。</summary>
    public string RestModeStatusText
        => _restMode.IsArmed
            ? (_restMode.IsSessionActive ? L("Str.RestStatusResting") : L("Str.RestStatusArmed"))
            : L("Str.RestStatusOff");

    /// <summary>把电源通知（离开模式/睡眠恢复）挂接到主窗口。</summary>
    public void AttachRestModeWindow(System.Windows.Window window) => _restMode.AttachWindow(window);

    /// <summary>显示拓扑变化：通知显示执行器（串流结束的瞬间立即恢复压黑，丝滑过渡）。</summary>
    public void OnDisplayTopologyChanged() => _displayExecutor.OnDisplayTopologyChanged();

    /// <summary>
    /// fail-open：若 Guardian 曾关过屏且当前无保护/待机请求，强制点亮并把屏幕交还用户。
    /// 应用退出与启动救援共用；无标记时不做任何动作（不打扰用户自己的电源设置）。
    /// </summary>
    public void ForceDisplayWakeIfNeeded()
    {
        if (!App.DisplayMayBeOff())
        {
            return;
        }
        App.DiagLog("[display] force wake (fail-open restore)");
        _displayExecutor.ForceWake();
        App.ClearDisplayMayBeOff();
    }

    /// <summary>后台执行 IPC 上报：失败静默（服务不可达时下次状态变化会重试）。</summary>
    private async Task ReportToServiceSafe(Func<Task> report)
    {
        try
        {
            await report();
        }
        catch
        {
            // 服务不可达：静默，等待下次状态变化重试。
        }
    }

    private void RefreshRestModeVisuals()
    {
        OnPropertyChanged(nameof(IsRestModeArmed));
        OnPropertyChanged(nameof(RestModeStatusText));
    }

    private async Task SetRestModeAsync(bool enable)
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            if (enable)
            {
                await _client.EnableRestModeAsync("Guardian.App");
            }
            else
            {
                await _client.DisableRestModeAsync("Guardian.App");
            }
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
        }
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                OnPropertyChanged(nameof(CanOperate));
            }
        }
    }

    /// <summary>服务已连接且无进行中的命令时才允许操作。</summary>
    public bool CanOperate => !IsBusy && _connectionStatus == ConnectionStatus.Connected;

    // ---------- 保护状态 ----------

    public ProtectionState CurrentState
    {
        get => _currentState;
        private set
        {
            if (SetProperty(ref _currentState, value))
            {
                RefreshStateText();
                RefreshToggle();
            }
        }
    }

    public ProtectionMode ActiveMode
    {
        get => _activeMode;
        private set
        {
            if (SetProperty(ref _activeMode, value))
            {
                RefreshModeText();
            }
        }
    }

    public string StateText
    {
        get => _stateText;
        private set => SetProperty(ref _stateText, value);
    }

    public Brush StateBrush
    {
        get => _stateBrush;
        private set => SetProperty(ref _stateBrush, value);
    }

    public string ActiveModeText
    {
        get => _activeModeText;
        private set => SetProperty(ref _activeModeText, value);
    }

    public string LastOperationText
    {
        get => _lastOperationText;
        private set => SetProperty(ref _lastOperationText, value);
    }

    public bool IsProtectionActive
    {
        get => _isProtectionActive;
        private set => SetProperty(ref _isProtectionActive, value);
    }

    public string ToggleButtonText
    {
        get => _toggleButtonText;
        private set => SetProperty(ref _toggleButtonText, value);
    }

    public string EmergencyHotkeyText
        => string.Format(_localization.L("Str.EmergencyRecoverHotkeyNote"),
            "Ctrl+Alt+Shift+" + (_serverSettings?.EmergencyHotkeyKey ?? "F12"));

    // ---------- 模式选择 ----------

    public ProtectionMode SelectedMode
    {
        get => _selectedMode;
        private set => SetProperty(ref _selectedMode, value);
    }

    public bool IsPrivacy
    {
        get => _isPrivacy;
        set
        {
            if (SetProperty(ref _isPrivacy, value) && value)
            {
                SelectedMode = ProtectionMode.Privacy;
            }
        }
    }

    public bool IsInputIsolation
    {
        get => _isInputIsolation;
        set
        {
            if (SetProperty(ref _isInputIsolation, value) && value)
            {
                SelectedMode = ProtectionMode.InputIsolation;
            }
        }
    }

    public bool IsEnhanced
    {
        get => _isEnhanced;
        set
        {
            if (SetProperty(ref _isEnhanced, value) && value)
            {
                SelectedMode = ProtectionMode.EnhancedIsolation;
            }
        }
    }

    // ---------- 公开方法 ----------

    /// <summary>后台加载能力与服务端设置，并触发一次状态刷新。</summary>
    public async Task InitializeAsync()
    {
        try
        {
            _capabilities = await _client.GetCapabilitiesAsync();
            _serverSettings = await _client.GetSettingsAsync();
            OnPropertyChanged(nameof(EmergencyHotkeyText));
            var hotkeyVk = HotkeyMap.Resolve(_serverSettings?.EmergencyHotkeyKey ?? "F12");
            EmergencyHotkeyKeyChanged?.Invoke(hotkeyVk);
            _restMode.SetEmergencyVk(hotkeyVk);
        }
        catch (GuardianClientException)
        {
            // 能力加载失败不阻塞界面，后续命令按需处理。
        }
        await RefreshStatusAsync();

        // 紧急键 UI 轮询通道（重写）：DispatcherTimer 运行在 UI 线程（有消息循环），
        // GetAsyncKeyState 查询可靠。此前放在线程池 Timer 上查询修饰键状态不可靠
        // （无消息循环的线程读到的异步键状态可能陈旧），实测轮询通道失声。
        // 与钩子识别、RegisterHotKey 变体注册并存，三条通道互为冗余。
        _emergencyUiPoll = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _emergencyUiPoll.Tick += (_, _) => PollEmergencyOnUiThread();
        _emergencyUiPoll.Start();

        // 窗口可见性同步：可见 = 用户操作界面（屏幕点亮），隐藏 = 压黑。
        _windowVisibilityTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _windowVisibilityTimer.Tick += (_, _) => SyncWindowVisibility();
        _windowVisibilityTimer.Start();

        // 拓扑上报必须一直在跑：服务端的枚举互锁只信新鲜上报，断了上报一键就会被拦住。
        _streamingEnv.Start();
    }

    private long _lastUiPollEmergencyTick;

    /// <summary>
    /// UI 线程轮询紧急组合键（任意修饰键 + 主键）：与钩子识别、RegisterHotKey
    /// 多组合变体并存。2 秒防抖。
    /// </summary>
    private void PollEmergencyOnUiThread()
    {
        try
        {
            if (!RestModeSupport.IsEmergencyComboPressed((uint)_restMode.EmergencyVk))
            {
                return;
            }
            var now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastUiPollEmergencyTick) < 2000)
            {
                return;
            }
            Interlocked.Exchange(ref _lastUiPollEmergencyTick, now);
            App.DiagLog("hotkey PRESSED (ui-poll)");
            EmergencyRecoverFromHotkey();
        }
        catch
        {
            // 单次轮询失败忽略。
        }
    }

    private System.Windows.Window? _mainWindow;
    private DispatcherTimer? _emergencyUiPoll;
    private DispatcherTimer? _windowVisibilityTimer;

    /// <summary>保存主窗口引用：保护开启时自动最小化 + 可见性同步。</summary>
    public void AttachMainWindow(System.Windows.Window window) => _mainWindow = window;

    private void SyncWindowVisibility()
    {
        try
        {
            // 注意：WPF 最小化只改 WindowState，IsVisible 仍为 true——
            // 必须排除最小化状态，否则“开保护自动最小化”后暂缓永不解除（屏幕不黑）。
            var w = _mainWindow;
            var visible = w != null
                && w.IsVisible
                && w.WindowState != WindowState.Minimized;
            _displayExecutor.SetWindowVisible(visible);
        }
        catch
        {
            // 单次同步失败忽略。
        }
    }

    /// <summary>
    /// 连接成功后上报一次交互会话输入设备清单：
    /// 服务运行于 Session 0，无法枚举交互会话的 Raw Input 设备。
    /// </summary>
    private async Task ReportInputDevicesOnceAsync()
    {
        if (_devicesReported)
        {
            return;
        }
        _devicesReported = true;
        try
        {
            var devices = InputCapabilityDetector.Detect();
            if (devices.Count > 0)
            {
                await _client.ReportInputDevicesAsync(devices);
            }
        }
        catch (GuardianClientException)
        {
            // 上报失败不阻塞界面；下次启动会重试。
        }
    }

    /// <summary>应用服务端快照（UI 唯一真实来源）。</summary>
    public void ApplySnapshot(ProtectionSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            MarkDisconnected();
            return;
        }

        // 乱序快照防护：屏黑期间心跳/命令响应会被拖慢数秒，先发出、后返回的旧快照若被照单全收，
        // 会把紧急恢复刚点亮的屏幕重新压黑（实测：覆盖解除 13ms 后即被过期快照二次关屏）。
        if (snapshot.StateStampMs < _lastSnapshotStampMs)
        {
            App.DiagLog(
                $"stale snapshot ignored: stamp={snapshot.StateStampMs} < applied={_lastSnapshotStampMs} privacy={snapshot.DisplayPrivacyActive}");
            return;
        }
        _lastSnapshotStampMs = snapshot.StateStampMs;

        _snapshot = snapshot;
        ConnectionStatus = ConnectionStatus.Connected;
        ServiceVersionText = string.IsNullOrEmpty(snapshot.ServiceVersion)
            ? string.Empty
            : $"v{snapshot.ServiceVersion}";
        CurrentState = snapshot.State;
        ActiveMode = snapshot.ActiveMode;
        RefreshLastOperation();
        UpdateTiles();

        // 显示隐私由客户端在交互会话执行（服务端跨会话广播不可靠）。
        var displayActive = snapshot.DisplayPrivacyActive;

        // 输入隔离同样由客户端执行（Session 0 钩子无效）。
        var inputActive = snapshot.InputIsolationActive;

        if (_emergencyLocalOverride)
        {
            // 紧急恢复的覆盖窗口内：忽略旧快照的保护标志，本地保持恢复状态，杜绝闪回黑屏。
            displayActive = false;
            inputActive = false;
            if (!snapshot.DisplayPrivacyActive)
            {
                // 服务端已确认保护解除：事件驱动解除覆盖（固定时长不可靠——屏黑期间
                // 低功率定时器合并会把心跳拖慢数秒，固定 3 秒覆盖可能过早过期）。
                _emergencyLocalOverride = false;
            }
        }

        _displayExecutor.Apply(displayActive);
        ApplyInputIsolation(inputActive);
        // 保护生效期间输入钩子也运行：吞掉会点亮 DPMS 屏的物理输入（防闪屏），
        // 且紧急组合键由钩子自备识别（不依赖 RegisterHotKey 与 UI 消息泵）。
        _restMode.SetProtectionGuard(displayActive || inputActive);

        // 记录“Guardian 关过屏”：进程异常退出/重启后据此把屏幕交还用户（fail-open）。
        if (displayActive || snapshot.RestModeActive)
        {
            App.MarkDisplayMayBeOff();
        }

        // 启动救援：上一次运行把屏幕留在熄灭状态，而当前没有任何保护/待机请求 → 强制点亮。
        if (!_startupDisplayRescueDone)
        {
            _startupDisplayRescueDone = true;
            if (!displayActive && !snapshot.RestModeActive && App.DisplayMayBeOff())
            {
                App.DiagLog("startup: display may be off, force wake");
                _ = Task.Run(ForceDisplayWakeIfNeeded);
            }
        }

        // 待机模式：按服务端快照同步武装状态（服务端是权威）。
        // 但本次运行的第一个快照例外：那时武装状态是从上次运行恢复来的，
        // 顺手进待机会话等于"打开应用就黑屏"（用户实测报告）。用户自己勾选的生效走后续快照。
        var firstRestSync = !_restModeSyncDone;
        _restModeSyncDone = true;
        _restMode.SetArmed(snapshot.RestModeActive, enterSessionOnArm: !firstRestSync);

        // 保护解除后若待机已武装，恢复待机会话（“解除串流和保护后恢复待机状态”）。
        // 紧急恢复覆盖窗口内除外——紧急已主动退出待机会话并锁屏，旧快照不得把会话重新打开。
        if (!_emergencyLocalOverride
            && _lastProtectionActive
            && !snapshot.DisplayPrivacyActive
            && _restMode.IsArmed)
        {
            _restMode.EnterSession();
        }
        if (!_emergencyLocalOverride)
        {
            // “关→开”边沿自动最小化主窗口：防输入穿透点中开关，且让压黑不受“窗口可见暂缓”干扰。
            // 待机会话必须同样处理——窗口保持可见时显示执行器判定“用户在操作界面”而暂缓压黑并点亮屏幕，
            // 结果“开了待机屏不黑，动一下键鼠才锁屏”。
            var restSessionActive = _restMode.IsSessionActive;
            var protectionEntered = !_lastProtectionActive && snapshot.DisplayPrivacyActive;
            var sessionEntered = !_lastRestSessionActive && restSessionActive;
            if ((protectionEntered || sessionEntered) && _mainWindow is { } w)
            {
                w.WindowState = WindowState.Minimized;
            }
            _lastProtectionActive = snapshot.DisplayPrivacyActive;
            _lastRestSessionActive = restSessionActive;
        }

        UpdateStreamingNote();

        _ = ReportInputDevicesOnceAsync();

        if (snapshot.ActiveSessionCount != _lastSessionCount)
        {
            _lastSessionCount = snapshot.ActiveSessionCount;
            _ = RefreshSessionsAsync();
        }
    }

    /// <summary>显示隐私因串流会话暂缓/恢复时刷新磁贴（回调可能来自线程池，须调度回 UI 线程）。</summary>
    private void OnDisplaySuppressionChanged(bool suppressed)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            ApplyDisplaySuppression(suppressed);
        }
        else
        {
            dispatcher.BeginInvoke(() => ApplyDisplaySuppression(suppressed));
        }
    }

    private void ApplyDisplaySuppression(bool suppressed)
    {
        _displaySuppressed = suppressed;
        UpdateTiles();
    }

    /// <summary>
    /// 紧急热键（Ctrl+Alt+Shift+主键）触发：先本地恢复显示与输入（不依赖服务端往返，fail-open 最快路径），
    /// 再通知服务端执行全面恢复。不走确认对话框——紧急路径不允许阻塞。
    /// 本地恢复后开启 3 秒快照覆盖：服务端恢复完成前的旧快照（保护仍=开启）会导致
    /// 屏幕被重新关掉，表现为“一闪恢复→又黑屏→闪回”。
    /// </summary>
    public void EmergencyRecoverFromHotkey()
    {
        // 紧急键 = 最高优先级：本地立即解除一切（显示 + 输入隔离），并立刻放弃
        // “保护解除后恢复待机”的判定（_lastProtectionActive 置 false，避免覆盖窗口
        // 过期后旧状态把待机会话/屏幕重新打开）。两个边沿标记同时归零，
        // 保证覆盖窗口结束后“再次进入保护/待机”仍能触发作息自动最小化。
        _emergencyLocalOverride = true;
        _lastProtectionActive = false;
        _lastRestSessionActive = false;
        _displayExecutor.Apply(false);
        TryReclaimPhysicalPrimary();
        TryDisableInputIsolation();
        _restMode.OnEmergency();
        _ = EmergencyRecoverViaServiceAsync();

        // 覆盖窗口事件驱动收尾：服务端快照确认 protection=false 时立即解除（ApplySnapshot 内），
        // 此处仅作 30 秒硬超时兜底。固定 3 秒不可靠——屏幕熄灭期间 Windows 进入
        // 低功率定时器合并，服务端命令与心跳都可能被拖慢数秒（实测 9 秒才送达）。
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            _emergencyLocalOverride = false;
        });
    }

    private async Task EmergencyRecoverViaServiceAsync()
    {
        try
        {
            await _client.RecoverAsync(RecoveryReason.EmergencyHotkey, RecoverySource.Local, "紧急热键");
        }
        catch (GuardianClientException)
        {
            // 服务端不可达时本地已恢复显示；心跳恢复兜底。
        }
        await RefreshStatusAsync();
    }

    /// <summary>连接失败时的处理：判定服务状态并置界面为“未知”。</summary>
    public void MarkDisconnected()
    {
        var status = WindowsServiceManager.GetStatus();
        ConnectionStatus newStatus;
        if (status is null)
        {
            newStatus = ConnectionStatus.ServiceNotInstalled;
        }
        else if (status != ServiceControllerStatus.Running)
        {
            newStatus = ConnectionStatus.ServiceNotRunning;
        }
        else
        {
            newStatus = ConnectionStatus.Disconnected;
        }

        // 状态未变化时避免重复刷新。
        if (newStatus == _connectionStatus)
        {
            return;
        }

        _snapshot = null;
        _emergencyLocalOverride = false;
        _lastSnapshotStampMs = 0;
        CurrentState = ProtectionState.Unknown;
        ActiveMode = ProtectionMode.None;
        ServiceVersionText = string.Empty;
        ConnectionStatus = newStatus;
        RefreshLastOperation();
        UpdateTiles();
        _lastSessionCount = -1;
        ClearSessions();
        ShowStreamingNote = false;

        // 服务失联：立即恢复显示与输入（客户端仍在交互会话，fail-open 由本进程兜底）。
        _displayExecutor.Apply(false);
        TryDisableInputIsolation();
    }

    /// <summary>
    /// 串流提示：串流期间物理屏由串流端（Sunshine 虚拟显示器）关闭，
    /// 此时“黑屏 + 未启动”是正常组合，必须在界面如实说明，避免用户误以为保护已开启或状态错误。
    /// </summary>
    private void UpdateStreamingNote()
    {
        var streaming = false;
        try
        {
            streaming = _streaming.IsStreamingActive();
        }
        catch
        {
            // 检测失败按无串流处理。
        }

        var protectionActive = CurrentState is ProtectionState.Active or ProtectionState.Activating;
        ShowStreamingNote = streaming && !protectionActive;
    }

    /// <summary>按快照同步输入隔离钩子（客户端交互会话内执行，钩子安装失败不阻塞主流程）。</summary>
    private void ApplyInputIsolation(bool active)
    {
        try
        {
            if (active && !_inputIsolation.IsActive)
            {
                _inputIsolation.Enable(
                    OnEmergencyFromInputHook,
                    _serverSettings?.EmergencyHotkeyKey ?? "F12");
            }
            else if (!active && _inputIsolation.IsActive)
            {
                _inputIsolation.Disable();
            }
        }
        catch (Exception ex)
        {
            App.DiagLog($"input isolation apply failed: {ex.Message}");
        }
    }

    private void TryDisableInputIsolation()
    {
        try
        {
            if (_inputIsolation.IsActive)
            {
                _inputIsolation.Disable();
            }
        }
        catch (Exception ex)
        {
            App.DiagLog($"input isolation disable failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 隔离钩子内部识别到紧急组合键（钩子线程回调）：
    /// 先本地恢复显示（线程安全），再把服务端恢复调度回 UI 线程。
    /// </summary>
    private void OnEmergencyFromInputHook()
    {
        _displayExecutor.Apply(false);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(EmergencyRecoverFromHotkey);
        }
        else
        {
            EmergencyRecoverFromHotkey();
        }
    }

    // ---------- 交互逻辑 ----------

    private async Task ToggleProtectionAsync()
    {
        if (IsBusy)
        {
            return;
        }
        if (_snapshot is not null && _snapshot.State is ProtectionState.Active or ProtectionState.Activating)
        {
            await DisableProtectionAsync();
        }
        else
        {
            await EnableProtectionAsync();
        }
    }

    private async Task EnableProtectionAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            var mode = SelectedMode;

            // 首次（未验证）启用前需确认显示隐私检查。
            if (mode is ProtectionMode.Privacy or ProtectionMode.InputIsolation)
            {
                var validated = _capabilities?.DisplayPrivacyValidated;
                if (validated != true)
                {
                    var dlg = new DisplayPrivacyCheckDialog { Owner = Application.Current.MainWindow };
                    if (dlg.ShowDialog() != true)
                    {
                        return;
                    }
                }
            }

            // 输入隔离需双重确认。
            if (mode == ProtectionMode.InputIsolation)
            {
                if (!ConfirmInputIsolation())
                {
                    return;
                }
            }

            await _client.EnableProtectionAsync(mode, "Guardian.App", displayPrivacyCheckAcknowledged: true);

            // 首次显示隐私启用后，询问远程画面是否正常。
            if (_capabilities?.DisplayPrivacyValidated != true)
            {
                await AskRemoteCaptureAsync();
            }
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
            await RefreshSessionsAsync();
        }
    }

    private bool ConfirmInputIsolation()
    {
        var remoteRejected = _snapshot?.RemoteSessionActive == true
            && _serverSettings?.AllowIsolationDuringRemoteSession != true;
        var dlg = new IsolationConfirmDialog(remoteRejected) { Owner = Application.Current.MainWindow };
        return dlg.ShowDialog() == true;
    }

    private async Task AskRemoteCaptureAsync()
    {
        var result = MessageBox.Show(
            _localization.L("Str.RemoteCaptureOkMessage"),
            _localization.L("Str.RemoteCaptureOkTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        var ok = result == MessageBoxResult.Yes;
        try
        {
            await _client.ConfirmDisplayPrivacyCheckAsync(ok, ok ? null : "用户反馈远程画面异常");
            if (!ok)
            {
                await _client.RecoverAsync(RecoveryReason.UserRequest, RecoverySource.Local, "远程画面异常，用户手动恢复");
            }
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
    }

    public async Task DisableProtectionAsync()
    {
        if (IsBusy)
        {
            return;
        }
        IsBusy = true;
        try
        {
            await _client.DisableProtectionAsync("Guardian.App");
            var snapshot = await _client.GetStatusAsync();
            if (snapshot is not null && snapshot.ActiveSessionCount > 0)
            {
                MessageBox.Show(
                    string.Format(_localization.L("Str.ProtectionHeldBySessions"), snapshot.ActiveSessionCount),
                    _localization.L("Str.ProtectionStatus"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
            await RefreshSessionsAsync();
        }
    }

    public async Task EnablePrivacyFromTrayAsync()
    {
        // 托盘“开启保护（隐私）”固定使用隐私模式。
        IsPrivacy = true;
        IsInputIsolation = false;
        await EnableProtectionAsync();
    }

    public async Task EmergencyRecoverAsync()
    {
        if (IsBusy)
        {
            return;
        }
        var result = MessageBox.Show(
            _localization.L("Str.EmergencyRecoverConfirm"),
            _localization.L("Str.EmergencyRecover"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _client.RecoverAsync(RecoveryReason.UserRequest, RecoverySource.Local, null);
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        finally
        {
            IsBusy = false;
            await RefreshStatusAsync();
            await RefreshSessionsAsync();
        }
    }

    private void OpenSettings()
    {
        var vm = new SettingsViewModel(_client, _appSettings, _localization, _theme);
        var window = new SettingsWindow(vm) { Owner = Application.Current.MainWindow };
        window.ShowDialog();
    }

    private async Task OpenLogsAsync()
    {
        var window = new LogsWindow(_client) { Owner = Application.Current.MainWindow };
        window.Show();
        await window.LoadAsync();
    }

    private async Task CreateSessionAsync()
    {
        var dlg = new CreateSessionDialog { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true)
        {
            return;
        }
        try
        {
            await _client.CreateSessionAsync(dlg.Payload!);
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        await RefreshSessionsAsync();
        await RefreshStatusAsync();
    }

    private async Task EndSessionAsync(SessionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }
        try
        {
            await _client.EndSessionAsync(item.Id, SessionEndReason.UserRequest);
        }
        catch (GuardianClientException ex)
        {
            ShowError(ex.Message);
        }
        await RefreshSessionsAsync();
        await RefreshStatusAsync();
    }

    private async Task InstallOrStartServiceAsync()
    {
        var script = FindInstallScript();
        if (script is null)
        {
            MessageBox.Show(
                _localization.L("Str.InstallScriptNotFound"),
                _localization.L("Str.ServiceBannerTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!WindowsServiceManager.RunElevatedPowerShell(script, string.Empty, out var error))
        {
            ShowError(string.IsNullOrEmpty(error) ? _localization.L("Str.ServiceStartFailed") : error);
            return;
        }

        // 等待服务就绪后刷新状态。
        await Task.Delay(3000);
        await RefreshStatusAsync();
    }

    private static string? FindInstallScript()
    {
        var first = Path.Combine(AppContext.BaseDirectory, "scripts", "install.ps1");
        if (File.Exists(first))
        {
            return first;
        }

        // 开发场景：exe 目录上溯 4 层查找 installer\install.ps1。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 4 && dir.Parent is not null; i++)
        {
            dir = dir.Parent;
        }
        var second = Path.Combine(dir.FullName, "installer", "install.ps1");
        return File.Exists(second) ? second : null;
    }

    // ---------- 状态刷新 ----------

    private async Task RefreshStatusAsync()
    {
        try
        {
            var snapshot = await _client.GetStatusAsync();
            ApplySnapshot(snapshot);
        }
        catch (GuardianClientException)
        {
            MarkDisconnected();
        }
    }

    private async Task RefreshSessionsAsync()
    {
        try
        {
            var sessions = await _client.ListSessionsAsync();
            var active = sessions.Where(s => s.Status == SessionStatus.Active).ToList();
            SynchronizeSessions(active);
            if (_snapshot is not null)
            {
                _lastSessionCount = active.Count;
            }
        }
        catch (GuardianClientException)
        {
            // 会话刷新失败忽略，下次轮询重试。
        }
    }

    private void SynchronizeSessions(List<SessionInfo> active)
    {
        _sessions.Clear();
        foreach (var info in active)
        {
            _sessions.Add(new SessionItemViewModel(info, _localization));
        }
    }

    private void ClearSessions() => _sessions.Clear();

    // ---------- 磁贴 ----------

    private void InitializeTiles()
    {
        _tiles.Clear();
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE7F4", TitleKey = "Str.TilePrivacy" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE72E", TitleKey = "Str.TileInputIsolation" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE7E8", TitleKey = "Str.TilePower" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE716", TitleKey = "Str.TileActiveSessions" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE850", TitleKey = "Str.TilePowerSource" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE8AF", TitleKey = "Str.TileRemoteSession" });
        _tiles.Add(new StatusTileViewModel { Glyph = "\uE840", TitleKey = "Str.TileMonitors" });
    }

    private void UpdateTiles()
    {
        var s = _snapshot;
        if (s is null)
        {
            foreach (var tile in _tiles)
            {
                tile.Value = _localization.L("Str.UnknownShort");
                tile.ValueBrush = GetBrush("DisabledTextBrush");
            }
            return;
        }

        // 显示隐私：串流会话期间客户端会暂缓关屏（避免破坏 GPU 帧捕获），
        // 此时必须如实显示“暂缓”而非“生效中”，符合“界面状态与实际一致”原则。
        string displayValue;
        Brush displayBrush;
        if (s.DisplayPrivacyActive && _displaySuppressed)
        {
            displayValue = L("Str.Suspended");
            displayBrush = GetBrush("WarningBrush");
        }
        else
        {
            displayValue = s.DisplayPrivacyActive ? L("Str.Active") : L("Str.InactiveValue");
            displayBrush = s.DisplayPrivacyActive ? GetBrush("SuccessBrush") : GetBrush("SecondaryTextBrush");
        }
        SetTile(0, displayValue, displayBrush);

        SetTile(1, s.InputIsolationActive ? L("Str.Active") : L("Str.InactiveValue"),
            s.InputIsolationActive ? GetBrush("SuccessBrush") : GetBrush("SecondaryTextBrush"));

        string power;
        Brush powerBrush;
        if (s.PowerRequestActive)
        {
            if (s.PowerRequestSuspended)
            {
                power = L("Str.Suspended");
                powerBrush = GetBrush("WarningBrush");
            }
            else
            {
                power = L("Str.Active");
                powerBrush = GetBrush("SuccessBrush");
            }
        }
        else
        {
            power = L("Str.InactiveValue");
            powerBrush = GetBrush("SecondaryTextBrush");
        }
        SetTile(2, power, powerBrush);

        SetTile(3, s.ActiveSessionCount.ToString(), GetBrush("AccentBrush"));

        var powerSource = s.AcOnline
            ? L("Str.ACPower")
            : s.BatteryPercent.HasValue
                ? string.Format(L("Str.BatteryPower"), s.BatteryPercent.Value)
                : L("Str.Battery");
        SetTile(4, powerSource, GetBrush("SecondaryTextBrush"));

        SetTile(5, s.RemoteSessionActive ? L("Str.Remote") : L("Str.Local"),
            s.RemoteSessionActive ? GetBrush("AccentBrush") : GetBrush("SecondaryTextBrush"));

        SetTile(6, s.MonitorCount.ToString(), GetBrush("SecondaryTextBrush"));
    }

    private void SetTile(int index, string value, Brush brush)
    {
        var tile = _tiles[index];
        tile.Value = value;
        tile.ValueBrush = brush;
    }

    // ---------- 视觉 / 文案 ----------

    private void RefreshStateText()
    {
        StateText = CurrentState switch
        {
            ProtectionState.Inactive => L("Str.StateInactive"),
            ProtectionState.Active => L("Str.StateActive"),
            ProtectionState.Activating => L("Str.StateActivating"),
            ProtectionState.Recovering => L("Str.StateRecovering"),
            ProtectionState.Failed => L("Str.StateFailed"),
            _ => L("Str.StateUnknown"),
        };
        StateBrush = CurrentState switch
        {
            ProtectionState.Active => GetBrush("SuccessBrush"),
            ProtectionState.Activating => GetBrush("AccentBrush"),
            ProtectionState.Recovering => GetBrush("WarningBrush"),
            ProtectionState.Failed => GetBrush("DangerBrush"),
            _ => GetBrush("SecondaryTextBrush"),
        };
    }

    private void RefreshToggle()
    {
        IsProtectionActive = CurrentState is ProtectionState.Active or ProtectionState.Activating;
        ToggleButtonText = IsProtectionActive ? L("Str.DisableProtection") : L("Str.EnableProtection");
    }

    private void RefreshModeText()
    {
        ActiveModeText = ActiveMode switch
        {
            ProtectionMode.Privacy => L("Str.ModePrivacy"),
            ProtectionMode.InputIsolation => L("Str.ModeInputIsolation"),
            ProtectionMode.EnhancedIsolation => L("Str.ModeEnhancedIsolation"),
            _ => L("Str.ModeNone"),
        };
    }

    private void RefreshLastOperation()
    {
        var op = _snapshot?.LastOperation;
        if (string.IsNullOrWhiteSpace(op))
        {
            LastOperationText = L("Str.NoOperation");
            return;
        }
        var at = _snapshot?.LastOperationAt?.ToLocalTime();
        var timePart = at.HasValue ? $" · {at.Value:HH:mm:ss}" : string.Empty;

        // 恢复类操作显示人话（尤其“客户端失联自动恢复”，避免用户困惑保护为何消失）。
        var friendly = op switch
        {
            "recover" => FriendlyRecoveryText(_snapshot!.LastRecoveryReason),
            "startup-restore" => L("Str.OpStartupRestore"),
            _ => op,
        };
        LastOperationText = $"{friendly}{timePart}";
    }

    private string FriendlyRecoveryText(RecoveryReason? reason) => reason switch
    {
        RecoveryReason.ClientLost => L("Str.RecoveredClientLost"),
        RecoveryReason.EmergencyHotkey => L("Str.RecoveredEmergencyHotkey"),
        RecoveryReason.UserRequest => L("Str.RecoveredUserRequest"),
        RecoveryReason.AdapterFailure => L("Str.RecoveredAdapterFailure"),
        RecoveryReason.ServiceStop => L("Str.RecoveredServiceStop"),
        RecoveryReason.StartupRestore => L("Str.OpStartupRestore"),
        _ => L("Str.RecoveredGeneric"),
    };

    private void RefreshConnectionVisuals()
    {
        ConnectionText = ConnectionStatus switch
        {
            ConnectionStatus.Connected => L("Str.Connected"),
            ConnectionStatus.ServiceNotInstalled => L("Str.ServiceNotInstalled"),
            ConnectionStatus.ServiceNotRunning => L("Str.ServiceNotRunning"),
            _ => L("Str.Disconnected"),
        };
        ConnectionBrush = ConnectionStatus switch
        {
            ConnectionStatus.Connected => GetBrush("SuccessBrush"),
            ConnectionStatus.ServiceNotInstalled or ConnectionStatus.ServiceNotRunning => GetBrush("WarningBrush"),
            _ => GetBrush("DisabledTextBrush"),
        };
        ShowServiceBanner = ConnectionStatus is ConnectionStatus.ServiceNotInstalled or ConnectionStatus.ServiceNotRunning;
        BannerText = ConnectionStatus == ConnectionStatus.ServiceNotInstalled
            ? L("Str.ServiceBannerNotInstalled")
            : L("Str.ServiceBannerNotRunning");
    }

    /// <summary>主题或语言变化后刷新所有动态视觉与文案。</summary>
    private void RefreshDynamicContent()
    {
        RefreshStateText();
        RefreshToggle();
        RefreshModeText();
        RefreshLastOperation();
        RefreshConnectionVisuals();
        foreach (var tile in _tiles)
        {
            tile.Title = L(tile.TitleKey);
        }
        UpdateTiles();
        OnPropertyChanged(nameof(EmergencyHotkeyText));
        OnPropertyChanged(nameof(StreamingNoteText));
        OnPropertyChanged(nameof(RestModeStatusText));
    }

    private void ShowError(string? message)
    {
        MessageBox.Show(
            message ?? "未知错误",
            L("Str.ErrorTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private string L(string key) => _localization.L(key);

    private static Brush GetBrush(string key)
        => Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
}
