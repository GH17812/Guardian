using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Core.Engine;
using Guardian.Core.Ports;
using Guardian.Windows.Streaming;

namespace Guardian.Service;

/// <summary>
/// 串流环境的服务端编排：把"一键启用/一键卸载"跑成后台任务并立即返回，让界面能持续拿到分步进度。
/// 这一步必须异步：实测国内直连 GitHub 下载 Sunshine 安装包约 214 KB/s，33 MB 要跑 3 分钟，
/// 同步等会把 IPC 线程按住，界面在此期间既看不到进度也点不了取消，只能显示"卡死"。
/// </summary>
/// <summary>装配一键启用需要的设置读写：目录与"上次确认的虚拟屏标识"都必须现场读，不能锁在构造期。</summary>
public sealed record StreamingEnvSettings(
    Func<string> OfflineDirectory,
    Func<string?> LastOutputGuid,
    Action<string> RememberOutputGuid);

public sealed class StreamingEnvOrchestrator : IDisposable
{
    /// <summary>空闲时重复探测的最短间隔：一键过程会高频轮询，别把日志与 SetupAPI 读满。</summary>
    private static readonly TimeSpan ProbeCacheTtl = TimeSpan.FromSeconds(1);


    private readonly StreamingEnvCoordinator _coordinator;
    private readonly StreamingTopologyStore _topology;
    private readonly IGuardianLog _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly TimeSpan _restoreWait;

    /// <summary>接屏等待：客户端轮询周期 3 秒，留足一次往返 + 重新挂载拓扑的时间。</summary>
    private readonly TimeSpan _attachWait;

    private Task<StreamingEnvironmentProfile>? _running;
    private string _activity = string.Empty;
    private StreamingEnvironmentProfile? _cached;
    private long _cachedAtMs;

    public StreamingEnvOrchestrator(StreamingTopologyStore topology, IStreamingEnvironmentPort port, IClock clock, IGuardianLog log, TimeSpan? restoreWait = null, TimeSpan? attachWait = null)
    {
        _topology = topology;
        _log = log;
        // 客户端轮询间隔 1 秒，留足一次往返 + 重新挂载本身耗时（实测挂载流程要 2 秒左右）。
        _restoreWait = restoreWait ?? TimeSpan.FromSeconds(25);
        _attachWait = attachWait ?? TimeSpan.FromSeconds(60);
        _coordinator = new StreamingEnvCoordinator(port, clock, log);
        _coordinator.Progress += Publish;
    }

    /// <summary>真机装配：端口要回调本编排器才能把"夺回物理主显"落到交互会话，故用工厂而不是构造函数。</summary>
    public static StreamingEnvOrchestrator Create(StreamingTopologyStore topology, StreamingEnvSettings settings, IClock clock, IGuardianLog log)
    {
        var port = new StreamingEnvironmentService(topology.Current)
        {
            OfflineDirectoryProvider = () => string.IsNullOrWhiteSpace(settings.OfflineDirectory()) ? null : settings.OfflineDirectory(),
            LastKnownOutputGuid = () => string.IsNullOrWhiteSpace(settings.LastOutputGuid()) ? null : settings.LastOutputGuid(),
            RememberOutputGuid = guid => settings.RememberOutputGuid(guid),
        };

        var orchestrator = new StreamingEnvOrchestrator(topology, port, clock, log);

        // 播报与"夺回物理主显/接回虚拟屏"都要写实例状态，只能等编排器建好再挂上去。
        port.Activity = orchestrator.ReportActivity;
        port.RestorePhysical = orchestrator.RequestPhysicalRestoreAsync;
        port.AttachVirtualDisplay = orchestrator.RequestVirtualAttachAsync;
        return orchestrator;
    }

    /// <summary>耗时动作的过程播报：既落日志（事后能追时序），也进档案（界面当下看得见进度）。</summary>
    public void ReportActivity(string text)
    {
        _activity = text;
        _log.Write(GuardianLogLevel.Information, "streaming-env", text);
    }

    /// <summary>只读刷新。任务在跑时直接回最新进度，不再并发探测（探测会读到半途改动的系统状态）。</summary>
    public StreamingEnvironmentProfile Refresh()
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false })
            {
                // 运行中也要重新打戳：接屏/夺回主显的待办标志是在最后一次进度发布之后才置位的，
                // 直接回缓存会让客户端在整个等待窗口里都看不到待办——实测表现为 60 秒白等超时、
                // "已请求客户端接屏，但 60 秒内没有收到回执"。
                return Stamped(_cached ?? _coordinator.Current);
            }

            if (_cached is not null && Environment.TickCount64 - _cachedAtMs < ProbeCacheTtl.TotalMilliseconds)
            {
                return _cached;
            }
        }

        return Stamped(_coordinator.Refresh());
    }

    /// <summary>
    /// 发起一键动作，立即返回 Busy 档案。重复点击不会排队执行第二个：协调器内部有单飞闸，
    /// 这里也先回当前进度，避免界面以为"又点了一次就会更快"。
    /// </summary>
    public StreamingEnvironmentProfile Start(StreamingEnvOp op, string source)
    {
        lock (_sync)
        {
            if (_running is { IsCompleted: false })
            {
                return _cached ?? Stamped(_coordinator.Current);
            }

            _activity = $"已发起一键{(op == StreamingEnvOp.Enable ? "启用" : "卸载")}，正在探测环境…";
            _log.Write(GuardianLogLevel.Information, "streaming-env",
                $"开始一键{(op == StreamingEnvOp.Enable ? "启用" : "卸载")}", $"来源：{source}");

            _cached = Busy(op);
            _cachedAtMs = Environment.TickCount64;

            _running = Task.Run(() => op == StreamingEnvOp.Enable
                ? _coordinator.EnableAsync(_cts.Token)
                : _coordinator.RevertAsync(_cts.Token));
            return _cached;
        }
    }

    /// <summary>供 CLI 与测试使用：发起并等到结束。</summary>
    public async Task<StreamingEnvironmentProfile> ApplyAsync(StreamingEnvOp op, string source)
    {
        Start(op, source);
        Task toWait;
        lock (_sync)
        {
            toWait = _running ?? Task.CompletedTask;
        }

        try
        {
            await toWait.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(GuardianLogLevel.Error, "streaming-env", "一键动作异常终止", ex.Message);
        }

        return Refresh();
    }

    /// <summary>
    /// 收下客户端的拓扑上报，并回一份当前档案（界面靠这一趟往返看见状态与待办标志）。
    /// 这里必须走 Refresh() 而不是直接回 Current：Current 在第一次探测之前是空的，
    /// 而客户端每 3 秒的上报正是它拿到环境的唯一时机——直接回缓存会让界面永远显示"未探测"。
    /// Refresh() 自带两重保护：一键在跑时回分步进度、空闲时 1 秒 TTL，不会把探测打爆。
    /// </summary>
    public StreamingEnvironmentProfile ReportTopology(StreamingTopologyPayload payload)
    {
        _topology.Publish(new StreamingTopologyFact
        {
            VirtualDisplayEnumerated = payload.VirtualDisplayEnumerated,
            VirtualDisplayPathActive = payload.VirtualDisplayPathActive,
            PhysicalDisplayActive = payload.PhysicalDisplayActive,
            ActiveMonitorCount = payload.ActiveMonitorCount,
            ReportedAt = DateTimeOffset.Now,
        });

        return Refresh();
    }

    private void Publish(StreamingEnvironmentProfile profile)
    {
        lock (_sync)
        {
            _cached = Stamped(profile);
            _cachedAtMs = Environment.TickCount64;
        }
    }

    private StreamingEnvironmentProfile Stamped(StreamingEnvironmentProfile profile)
    {
        var copy = profile.Clone();
        copy.PhysicalRestorePending = _topology.RestorePending;
        copy.AttachVirtualDisplayPending = _topology.VirtualAttachPending;
        copy.ActivityText = _activity;
        return copy;
    }

    private StreamingEnvironmentProfile Busy(StreamingEnvOp op)
    {
        var baseProfile = _coordinator.Current;
        return new StreamingEnvironmentProfile
        {
            State = StreamingEnvState.Busy,
            GeneratedAt = DateTimeOffset.Now,
            StampMs = Environment.TickCount64,
            Sunshine = baseProfile.Sunshine,
            VirtualDisplay = baseProfile.VirtualDisplay,
            Steps = baseProfile.Steps,
            RunningOp = op,
            CanEnable = false,
            CanRevert = false,
            PhysicalRestorePending = _topology.RestorePending,
        };
    }

    /// <summary>
    /// 夺回物理主显必须发生在交互会话（服务端 Session 0 改不了显示拓扑），而管道只有请求/应答，
    /// 所以这里置一个待办标志、等客户端轮询看到后执行重新挂载并回执拓扑。等不到就如实报未确认，
    /// 绝不把"没确认"写成"已恢复"。
    /// </summary>
    /// <summary>
    /// 请求客户端把虚拟屏接进桌面布局。与"夺回物理主显"同一套机制：
    /// 置待办标志 → 等客户端执行后回执（新鲜上报且虚拟屏已进桌面）→ 超时如实报未确认。
    /// </summary>
    public async Task<StreamingEnvOutcome> RequestVirtualAttachAsync(CancellationToken token)
    {
        _topology.RequestVirtualAttach();

        var acknowledged = await _topology
            .WaitForVirtualEnumeratedAsync(_attachWait, token)
            .ConfigureAwait(false);

        return acknowledged
            ? new StreamingEnvOutcome(true, "客户端已把虚拟屏接进桌面布局，并确认可枚举。")
            : new StreamingEnvOutcome(false,
                $"已请求客户端接屏，但 {_attachWait.TotalSeconds:0} 秒内没有收到回执（虚拟屏仍不在桌面布局里）。"
                + "可用厂商工具 VDD Control 的 Connect This Device 手工接屏后再点一键启用。");
    }

    public async Task<StreamingEnvOutcome> RequestPhysicalRestoreAsync(CancellationToken token)
    {
        _topology.RequestPhysicalRestore();

        var acknowledged = await _topology
            .WaitForRestoreAcknowledgedAsync(_restoreWait, token)
            .ConfigureAwait(false);

        return acknowledged
            ? new StreamingEnvOutcome(true, "交互会话已重新挂载物理屏，并确认物理面板处于活动状态。")
            : new StreamingEnvOutcome(false,
                $"已请求交互会话重新挂载物理屏，但 {_restoreWait.TotalSeconds:0} 秒内没有收到回执（客户端可能已关闭）。"
                + "可重做一次一键卸载，或在本地用紧急恢复键交还控制权。");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
