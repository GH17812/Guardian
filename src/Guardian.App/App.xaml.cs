using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Guardian.App.Services;
using Guardian.App.ViewModels;
using Guardian.App.Views;
using Guardian.Windows.Display;
using Microsoft.Win32;

namespace Guardian.App;

/// <summary>应用程序入口：负责单实例、服务装配、主题与语言字典挂载、托盘与主窗口生命周期。</summary>
public partial class App : Application
{
    private const string MutexName = "Guardian.App.SingleInstance";
    private const string ActivateEventName = "Guardian.App.Activate";

    private Mutex? _mutex;
    private bool _ownsMutex;
    private EventWaitHandle? _activateEvent;

    private AppSettingsService? _appSettings;
    private LocalizationManager? _localization;
    private ThemeManager? _themeManager;
    private GuardianClientService? _client;
    private MainViewModel? _mainViewModel;
    private ServiceConnectionMonitor? _monitor;
    private TrayIconService? _tray;
    private MainWindow? _mainWindow;

    /// <summary>开机自启时以最小化窗口启动（不弹脸），由命令行 --minimized 决定。</summary>
    private bool _startMinimized;
    private EmergencyHotkeyService? _hotkey;

    /// <summary>窗口位置救援：显示拓扑恢复后把遗留在虚拟屏/离屏区域的窗口移回主显示器。</summary>
    private readonly OffscreenWindowRescue _windowRescue = new();

    /// <summary>是否正在真正退出（区别于“最小化到托盘”）。</summary>
    public static bool IsExiting { get; private set; }

    /// <summary>诊断日志：记录启动里程碑、异常与退出点，用于排查进程静默退出与热键注册问题。</summary>
    internal static void DiagLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Guardian");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "app-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断日志失败不影响主流程。
        }
    }

    /// <summary>
    /// “屏幕可能被 Guardian 关掉了”的标记文件。
    /// 用于进程异常退出/重启后把屏幕交还用户：只要标记存在且当前没有保护/待机，
    /// 启动或退出时都强制唤醒一次（fail-open），避免面板停在熄灭状态无人恢复。
    /// </summary>
    private static string DisplayOffMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Guardian",
        "display-off.marker");

    internal static void MarkDisplayMayBeOff()
    {
        try
        {
            var path = DisplayOffMarkerPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTime.Now.ToString("O"));
        }
        catch
        {
            // 标记失败不影响主流程。
        }
    }

    internal static bool DisplayMayBeOff()
    {
        try
        {
            return File.Exists(DisplayOffMarkerPath);
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearDisplayMayBeOff()
    {
        try
        {
            File.Delete(DisplayOffMarkerPath);
        }
        catch
        {
            // 清理失败不影响主流程。
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 开机自启走 --minimized：客户端必须开机就在场（紧急热键与显示隐私执行体都只能在交互会话生效），
        // 但不该在登录后弹一个窗口挡住桌面。此前安装器没注册自启，重启后这两个能力其实都不存在，
        // 而界面还在报"服务健康"——那是 fail-open 承诺上的漏洞。
        _startMinimized = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagLog($"AppDomain.UnhandledException: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
        {
            DiagLog($"DispatcherUnhandledException: {args.Exception}");
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DiagLog($"UnobservedTaskException: {args.Exception}");
            args.SetObserved();
        };

        _mutex = new Mutex(true, MutexName, out bool createdNew);
        _ownsMutex = createdNew;
        DiagLog($"startup: mutexCreated={createdNew}");
        if (!createdNew)
        {
            // 已有实例在运行：通知其激活窗口后退出本进程。
            DiagLog("startup: another instance owns mutex, exiting");
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch
            {
                // 忽略：无法通知时直接退出即可。
            }
            Shutdown();
            return;
        }

        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

        InitializeServices();

        _mainWindow = new MainWindow(_mainViewModel!);
        MainWindow = _mainWindow;

        // 紧急恢复热键必须在交互会话注册（服务端 Session 0 钩子监听不到交互会话按键）。
        _hotkey = new EmergencyHotkeyService();
        _hotkey.Pressed += () => _mainViewModel!.EmergencyRecoverFromHotkey();
        _hotkey.StatusChanged += DiagLog;
        _mainViewModel!.EmergencyHotkeyKeyChanged += vk => _hotkey!.UpdateKey(vk);

        _tray = new TrayIconService(
            _mainViewModel!,
            showMain: () => Dispatcher.Invoke(_mainWindow.ShowAndActivate),
            exit: ExitApplication);

        _mainWindow.Closing += OnMainWindowClosing;
        if (_startMinimized)
        {
            // 最小化而非隐藏：可见性同步逻辑已把"最小化"当作无人在看，压黑与暂缓判定都成立。
            _mainWindow.WindowState = WindowState.Minimized;
        }

        _mainWindow.Show();
        DiagLog(_startMinimized ? "startup: window shown minimized" : "startup: window shown");

        // 挂接须在窗口显示之后（PresentationSource 此时才可用）；默认 F12，服务端设置加载后更新。
        _hotkey.Attach(_mainWindow, 0x7B);

        // 待机模式电源通知（离开模式 / 睡眠恢复）挂接到主窗口。
        _mainViewModel!.AttachRestModeWindow(_mainWindow);
        _mainViewModel!.AttachMainWindow(_mainWindow);

        // 显示拓扑变化（串流结束恢复物理屏、插拔显示器）后救援离屏窗口。
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        ScheduleWindowRescue();

        ListenForActivate();
    }

    /// <summary>
    /// 拓扑恢复后，桌面程序会保留旧坐标：串流期间位于虚拟显示器上的窗口会落在物理屏之外，
    /// 表现为“任务栏里有图标、桌面上看不到窗口”，重启程序也不会恢复，必须由外部把它们移回主屏。
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        DiagLog("display settings changed");
        // 串流结束时 Sunshine 重新挂载物理屏（面板瞬间点亮）：立即重申关屏，避免过渡闪烁。
        _mainViewModel?.OnDisplayTopologyChanged();
        ScheduleWindowRescue();
    }

    /// <summary>延时执行救援：窗口重排由系统异步完成，分两次尝试（约 2s 与 7s）。</summary>
    private void ScheduleWindowRescue()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var delay in new[] { 2000, 5000 })
                {
                    await Task.Delay(delay);
                    var moved = _windowRescue.Rescue();
                    if (moved > 0)
                    {
                        DiagLog($"window rescue: moved={moved}");
                    }
                }
            }
            catch (Exception ex)
            {
                DiagLog($"window rescue failed: {ex.Message}");
            }
        });
    }

    /// <summary>初始化各服务与视图模型，并按正确顺序挂载主题与语言字典。</summary>
    private void InitializeServices()
    {
        _appSettings = new AppSettingsService();
        _localization = new LocalizationManager(_appSettings);
        _themeManager = new ThemeManager(_appSettings);
        _client = new GuardianClientService();

        // 先挂主题字典、再挂语言字典，确保窗口首次渲染即有正确的颜色与文案。
        _themeManager.Initialize();
        _localization.Initialize();

        _mainViewModel = new MainViewModel(_client, _appSettings, _localization, _themeManager);
        _monitor = new ServiceConnectionMonitor(_client, _mainViewModel);
        _monitor.Start();

        // 后台加载能力报告与服务端设置，不阻塞窗口显示。
        _ = _mainViewModel.InitializeAsync();
    }

    /// <summary>后台线程监听“激活窗口”事件，用于单实例下唤起已有窗口。</summary>
    private void ListenForActivate()
    {
        Task.Run(() =>
        {
            while (_activateEvent is not null && !IsExiting)
            {
                try
                {
                    _activateEvent.WaitOne();
                }
                catch
                {
                    break;
                }
                Dispatcher.Invoke(() => _mainWindow?.ShowAndActivate());
            }
        });
    }

    private void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (IsExiting)
        {
            return;
        }
        // 关闭主窗口时最小化到托盘，而非退出。
        e.Cancel = true;
        _mainWindow!.Hide();
        _tray!.ShowBalloonOnce();
    }

    private void ExitApplication()
    {
        DiagLog("ExitApplication called (tray exit)");
        IsExiting = true;
        _monitor?.Stop();
        _tray?.Dispose();
        _hotkey?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagLog($"OnExit: IsExiting={IsExiting} exitCode={e.ApplicationExitCode}");
        IsExiting = true;
        // fail-open：退出前把屏幕交还用户（仅当 Guardian 确实关过屏时）。
        _mainViewModel?.ForceDisplayWakeIfNeeded();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _monitor?.Stop();
        _tray?.Dispose();
        _hotkey?.Dispose();
        _activateEvent?.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // 已释放或未拥有时忽略。
            }
        }
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
