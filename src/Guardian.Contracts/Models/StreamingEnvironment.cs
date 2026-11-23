namespace Guardian.Contracts.Models;

/// <summary>串流环境总体状态（界面逐态取文案，不得由 UI 自行推断）。</summary>
public enum StreamingEnvState
{
    /// <summary>尚未探测。</summary>
    Unknown = 0,

    /// <summary>Sunshine 未安装。</summary>
    SunshineMissing = 1,

    /// <summary>未启用：Sunshine 不接管显示拓扑，物理屏亮、串流物理屏。</summary>
    Reverted = 2,

    /// <summary>已就绪：虚拟屏被枚举命中且 Sunshine 配置指向它。</summary>
    Ready = 3,

    /// <summary>中间态：配置已写但虚拟屏未命中（或设备未启用），界面必须给出继续或回退入口。</summary>
    PartialFailure = 4,

    /// <summary>分步动作执行中。</summary>
    Busy = 5,

    /// <summary>
    /// 第一阶段已生效：Sunshine 用 ensure_primary 管理虚拟屏，但物理屏尚未停用。
    /// 这是"启用被安全拦停在半程"的真实形态，不能报成 Ready（那样等于说本地已经黑屏接管）。
    /// </summary>
    Activating = 6,
}

/// <summary>串流环境的原子步骤标识。</summary>
public enum StreamingEnvStep
{
    /// <summary>Sunshine 本体可用。</summary>
    SunshineInstalled = 0,

    /// <summary>虚拟显示驱动包与根设备存在。</summary>
    VirtualDisplayDriver = 1,

    /// <summary>虚拟显示设备已启用（禁用状态下不删驱动）。</summary>
    VirtualDisplayEnabled = 2,

    /// <summary>虚拟屏被显示枚举真实命中（互锁依据）。</summary>
    VirtualDisplayEnumerated = 3,

    /// <summary>Sunshine 配置键已写入或已还原。</summary>
    SunshineConfigured = 4,

    /// <summary>Sunshine 服务已重启并就绪。</summary>
    SunshineRunning = 5,

    /// <summary>物理显示器已回到活动且为主显（回退方向的兜底）。</summary>
    PhysicalDisplayRestored = 6,

    /// <summary>第一阶段写入：ensure_primary 把虚拟屏拉进桌面，不动物理屏。</summary>
    SunshineActivating = 7,

    /// <summary>第二阶段写入：ensure_only_display 正式接管拓扑。</summary>
    SunshineTakeover = 8,

    /// <summary>把已存在显示路径的虚拟屏接进桌面布局（客户端执行，ChangeDisplaySettingsEx 同厂商工具）。</summary>
    VirtualDisplayAttached = 9,
}

/// <summary>往 Sunshine 配置里写哪种形态。三态而不是布尔：接管拆成两阶段后，"写配置"本身有两种前进方向。</summary>
public enum SunshineConfigMode
{
    /// <summary>还原为不接管拓扑（删掉或回写我们接管的键）。</summary>
    Revert = 0,

    /// <summary>ensure_primary：只把指定虚拟屏激活并设为主显，物理屏不停用。</summary>
    Activate = 1,

    /// <summary>ensure_only_display：只留指定虚拟屏，其余停用。</summary>
    Takeover = 2,
}

/// <summary>单步结果。</summary>
public enum StreamingEnvStepOutcome
{
    /// <summary>未执行。</summary>
    Pending = 0,

    /// <summary>成功。</summary>
    Ok = 1,

    /// <summary>失败。</summary>
    Failed = 2,

    /// <summary>被互锁拦下，未执行。</summary>
    Blocked = 3,

    /// <summary>无需执行（目标态已满足）。</summary>
    Skipped = 4,
}

/// <summary>串流环境操作意图。</summary>
public enum StreamingEnvOp
{
    /// <summary>一键启用：准备成虚拟屏 + Sunshine 接管拓扑的形态。</summary>
    Enable = 0,

    /// <summary>一键卸载：还原配置并禁用虚拟设备（驱动保留，下次启用无需重装）。</summary>
    Revert = 1,
}

/// <summary>单步执行记录。</summary>
public sealed class StreamingEnvStepResult
{
    public StreamingEnvStep Step { get; set; }

    public StreamingEnvStepOutcome Outcome { get; set; }

    /// <summary>面向用户的取证文本（实测值、路径、版本号），失败时必须能说明“停在哪一步”。</summary>
    public string Evidence { get; set; } = string.Empty;

    public DateTimeOffset At { get; set; }
}

/// <summary>Sunshine 侧探测结果。</summary>
public sealed class SunshineEnvironmentInfo
{
    public bool Installed { get; set; }

    public string Version { get; set; } = string.Empty;

    public bool ServiceRunning { get; set; }

    /// <summary>白名单配置键是否处于“接管拓扑”形态（磁盘上的文件内容）。</summary>
    public bool Configured { get; set; }

    /// <summary>
    /// 正在运行的 Sunshine 进程实际生效的形态。它与 <see cref="Configured"/> 不一致时属于危险中间态：
    /// 文件已还原但进程仍会接管拓扑，界面若只看文件就会把"还会停用物理屏"报成"已恢复"。
    /// </summary>
    public bool EffectiveConfigured { get; set; }

    /// <summary>
    /// 生效形态是"只激活"（ensure_primary）而不是"只留虚拟屏"。
    /// 有了它才能把"接管完成"与"安全地停在半程"区分开，界面上不能都写成已就绪。
    /// </summary>
    public bool Activating { get; set; }

    /// <summary>配置文件被外部（如 Sunshine Web UI）改写，我们的键已丢失。</summary>
    public bool ConfigDrifted { get; set; }

    /// <summary>安装包的来源说明：下载 URL 或离线目录中的文件名。</summary>
    public string? InstallSource { get; set; }
}

/// <summary>虚拟显示侧探测结果。</summary>
public sealed class VirtualDisplayEnvironmentInfo
{
    /// <summary>驱动包已进驱动库且根设备存在。</summary>
    public bool DevicePresent { get; set; }

    public bool DeviceEnabled { get; set; }

    /// <summary>
    /// 虚拟屏已经是"桌面里的一块输出"（交互会话 EnumDisplayMonitors 能看到）。
    /// 这是写 ensure_only_display 的前提：只有它真能当屏幕用，停用物理屏才不会让本机没有画面。
    /// </summary>
    public bool Enumerated { get; set; }

    /// <summary>
    /// 虚拟适配器存在 ACTIVE|ATTACHED 的显示路径（EnumDisplayDevices 级别，不要求在桌面里）。
    /// 实测：物理屏形态下路径常在、但不在桌面布局中——这正是 ensure_primary 要解决的中间态，
    /// 所以"能激活"与"已可用"必须分开判，否则一键启用结构性启不来。
    /// </summary>
    public bool PathActive { get; set; }

    public string InstanceId { get; set; } = string.Empty;

    public string FriendlyName { get; set; } = string.Empty;

    /// <summary>Sunshine output_name 需匹配的显示 GUID。</summary>
    public string? OutputGuid { get; set; }

    /// <summary>
    /// 物理显示器是否处于活动状态。null 表示交互会话没有新鲜上报，属于"未知"——
    /// 未知不能显示成"正常"，否则界面会在用户最需要的时候说谎。
    /// </summary>
    public bool? PhysicalDisplayActive { get; set; }

    /// <summary>虚拟屏标识来自 Sunshine 自己的设备清单（实测唯一可靠来源），用于界面取证展示。</summary>
    public string OutputGuidSource { get; set; } = string.Empty;
}

/// <summary>服务端留存的客户端拓扑上报（带到达时刻，用于新鲜度判定）。</summary>
public sealed record StreamingTopologyFact
{
    /// <summary>虚拟屏已进入桌面布局（可当屏幕用）。</summary>
    public bool VirtualDisplayEnumerated { get; init; }

    /// <summary>虚拟适配器有 ACTIVE|ATTACHED 显示路径（可被激活，但可能还不在桌面里）。</summary>
    public bool VirtualDisplayPathActive { get; init; }

    public bool PhysicalDisplayActive { get; init; }

    public int ActiveMonitorCount { get; init; }

    public DateTimeOffset ReportedAt { get; init; }
}

/// <summary>串流环境档案：探测结果 + 最近一次分步执行记录。</summary>
public sealed class StreamingEnvironmentProfile
{
    public StreamingEnvState State { get; set; }

    /// <summary>探测/推进时刻（本地）。</summary>
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>
    /// 单调生成戳（Environment.TickCount64）：分步执行会连续推送多份档案，
    /// 客户端需与快照一样丢弃迟到的旧档案，否则执行中的进度条会被旧档案倒回去。
    /// </summary>
    public long StampMs { get; set; }

    public SunshineEnvironmentInfo Sunshine { get; set; } = new();

    public VirtualDisplayEnvironmentInfo VirtualDisplay { get; set; } = new();

    public List<StreamingEnvStepResult> Steps { get; set; } = new();

    public StreamingEnvOp? RunningOp { get; set; }

    public string? BlockedReason { get; set; }

    public bool CanEnable { get; set; }

    /// <summary>
    /// 服务端已请求"把物理屏夺回主显"，等待交互会话执行并确认。
    /// 管道是请求/应答模型、没有服务端推送通道，所以这个标志就是服务端交给客户端的活：
    /// 客户端轮询看到它才做一次重新挂载，然后用拓扑上报回执。
    /// </summary>
    public bool PhysicalRestorePending { get; set; }

    /// <summary>
    /// 服务端请求客户端"把虚拟屏接进桌面布局"。路径存在但不在桌面里时，写任何接管配置都没用
    /// （实测：Sunshine 只在客户端连接时才动拓扑，连接失败则永远卡住）——接屏这步必须由交互会话自己做。
    /// </summary>
    public bool AttachVirtualDisplayPending { get; set; }

    /// <summary>
    /// 服务端耗时动作的过程文本（典型：下载进度）。
    /// 没有它，一键启用在"下载 33 MB"这几分钟里界面只能显示"处理中"和一排"待执行"，
    /// 与卡死无法区分 —— 新机实测正是这样被误认为出故障。
    /// </summary>
    public string ActivityText { get; set; } = string.Empty;

    /// <summary>浅拷贝，供编排层盖自己的标志而不改动上游探测结果（与 GuardianSettings.Clone 同法）。</summary>
    public StreamingEnvironmentProfile Clone() => (StreamingEnvironmentProfile)MemberwiseClone();

    public bool CanRevert { get; set; }
}
