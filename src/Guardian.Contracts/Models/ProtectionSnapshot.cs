namespace Guardian.Contracts.Models;

/// <summary>保护状态快照 —— UI 展示的唯一真实来源。</summary>
public sealed class ProtectionSnapshot
{
    /// <summary>保护引擎状态。</summary>
    public ProtectionState State { get; set; }

    /// <summary>
    /// 快照生成时刻（Environment.TickCount64，机器级单调）：客户端据此丢弃迟到的旧快照。
    /// 屏黑期间 IPC 响应会拖数秒、先发的请求后返回，乱序到达的旧快照会把刚点亮的屏幕重新压黑。
    /// </summary>
    public long StateStampMs { get; set; }

    /// <summary>当前生效模式（None 表示未启用）。</summary>
    public ProtectionMode ActiveMode { get; set; }

    /// <summary>手动保护是否开启。</summary>
    public bool ManualProtectionEnabled { get; set; }

    /// <summary>显示隐私是否生效。</summary>
    public bool DisplayPrivacyActive { get; set; }

    /// <summary>输入隔离是否生效。</summary>
    public bool InputIsolationActive { get; set; }

    /// <summary>电源保护请求是否生效。</summary>
    public bool PowerRequestActive { get; set; }

    /// <summary>电源请求是否因电池供电被临时挂起。</summary>
    public bool PowerRequestSuspended { get; set; }

    /// <summary>当前活动会话数量。</summary>
    public int ActiveSessionCount { get; set; }

    /// <summary>最近一次保护操作描述。</summary>
    public string? LastOperation { get; set; }

    /// <summary>最近一次保护操作时间（本地时间）。</summary>
    public DateTimeOffset? LastOperationAt { get; set; }

    /// <summary>最近一次保护操作来源。</summary>
    public string? LastOperationBy { get; set; }

    /// <summary>最近一次恢复的原因（仅当 <see cref="LastOperation"/> 为 recover 时有效）。</summary>
    public RecoveryReason? LastRecoveryReason { get; set; }

    /// <summary>服务端时间（本地）。</summary>
    public DateTimeOffset ServerTime { get; set; }

    /// <summary>服务启动时间（本地）。</summary>
    public DateTimeOffset ServiceStartedAt { get; set; }

    /// <summary>服务版本。</summary>
    public string ServiceVersion { get; set; } = string.Empty;

    /// <summary>是否处于远程会话（RDP）。</summary>
    public bool RemoteSessionActive { get; set; }

    /// <summary>交流电源在线。</summary>
    public bool AcOnline { get; set; }

    /// <summary>电池百分比（0-100，桌面机为 null）。</summary>
    public int? BatteryPercent { get; set; }

    /// <summary>显示器数量。</summary>
    public int MonitorCount { get; set; }

    /// <summary>待机模式是否开启（睡眠转为离开模式，保持串流可达）。</summary>
    public bool RestModeActive { get; set; }

    /// <summary>系统启动时刻（本地）。</summary>
    public DateTimeOffset SystemBootAt { get; set; }
}
