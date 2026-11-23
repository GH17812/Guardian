namespace Guardian.Contracts.Models;

/// <summary>服务端（保护引擎）设置。UI 语言与主题属于客户端设置，不在此列。</summary>
public sealed class GuardianSettings
{
    /// <summary>紧急恢复快捷键主键（修饰键固定为 Ctrl+Alt+Shift）。</summary>
    public string EmergencyHotkeyKey { get; set; } = "F12";

    /// <summary>UI 丢失（心跳超时）后的动作。</summary>
    public UiLossAction UiLossAction { get; set; } = UiLossAction.Recover;

    /// <summary>服务重启后的恢复策略。</summary>
    public ResumePolicy ResumePolicy { get; set; } = ResumePolicy.Restore;

    /// <summary>客户端丢失判定阈值（秒）。</summary>
    public int ClientLostThresholdSeconds { get; set; } = 15;

    /// <summary>隐私模式下是否关闭显示器（默认 true）。</summary>
    public bool DisplayOffOnPrivacy { get; set; } = true;

    /// <summary>仅交流电源时保持系统运行（电池供电时挂起电源请求）。</summary>
    public bool OnlyOnAcPower { get; set; }

    /// <summary>手动保护的最长时长（分钟，0 表示不限制）。</summary>
    public int MaxManualProtectionMinutes { get; set; }

    /// <summary>允许在 RDP 远程会话期间启用实验性输入隔离（默认 false，安全优先）。</summary>
    public bool AllowIsolationDuringRemoteSession { get; set; }

    /// <summary>待机模式是否开启（服务重启后按此恢复离开模式请求）。</summary>
    public bool RestModeArmed { get; set; }

    /// <summary>日志级别。</summary>
    public GuardianLogLevel LogLevel { get; set; } = GuardianLogLevel.Information;

    /// <summary>
    /// 上次从 Sunshine 自己的设备清单里确认过的虚拟屏 output_name，连同取得时刻一起存。
    /// 物理屏形态下那条路径常常不在桌面布局里、日志清单也就只剩物理屏，没有这个缓存值一键启用
    /// 就结构性启不来；界面取证会写明是"沿用上次探测值"，不谎称当场命中。
    /// </summary>
    public string VirtualOutputGuid { get; set; } = string.Empty;

    /// <summary>VirtualOutputGuid 的取得时刻（本地时间，用于界面显示它有多旧）。</summary>
    public DateTimeOffset VirtualOutputGuidAt { get; set; }

    /// <summary>
    /// 一键启用的离线兜底目录：无网或国内下载太慢时，从这里取官方安装包（文件名须与钉死值一致）。
    /// 默认指向安装根下的 offline 目录（发布包已内置两个官方产物）；改成其他目录或清空则按设置走。
    /// </summary>
    public string StreamingEnvOfflineDirectory { get; set; } = DefaultOfflineDirectory;

    /// <summary>安装根下的 offline 目录：service 基目录是 ...\Guardian\service，app 是 ...\Guardian\app。</summary>
    public static string DefaultOfflineDirectory { get; } =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "offline"));

    public GuardianSettings Clone() => (GuardianSettings)MemberwiseClone();
}
