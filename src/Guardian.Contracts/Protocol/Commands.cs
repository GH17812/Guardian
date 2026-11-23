namespace Guardian.Contracts.Protocol;

/// <summary>IPC 命令名集合。命令名是稳定契约，新增命令不得复用旧名。</summary>
public static class Commands
{
    /// <summary>心跳 / 保活，同时刷新客户端在线状态。</summary>
    public const string Ping = "ping";

    /// <summary>获取完整保护状态快照。</summary>
    public const string GetStatus = "get-status";

    /// <summary>开启保护（需指定模式）。</summary>
    public const string EnableProtection = "enable-protection";

    /// <summary>解除保护（仅解除手动保护，不影响工作会话）。</summary>
    public const string DisableProtection = "disable-protection";

    /// <summary>紧急恢复：无条件释放全部保护并结束全部会话。</summary>
    public const string Recover = "recover";

    /// <summary>创建工作会话。</summary>
    public const string CreateSession = "create-session";

    /// <summary>结束工作会话。</summary>
    public const string EndSession = "end-session";

    /// <summary>列出全部会话。</summary>
    public const string ListSessions = "list-sessions";

    /// <summary>获取系统能力检测报告。</summary>
    public const string GetCapabilities = "get-capabilities";

    /// <summary>获取服务端设置。</summary>
    public const string GetSettings = "get-settings";

    /// <summary>更新服务端设置。</summary>
    public const string UpdateSettings = "update-settings";

    /// <summary>获取操作日志（最近 N 条）。</summary>
    public const string GetLogs = "get-logs";

    /// <summary>确认"关闭显示器后远程捕获是否正常"的验证结果。</summary>
    public const string ConfirmDisplayPrivacyCheck = "confirm-display-privacy-check";

    /// <summary>客户端上报交互会话输入设备清单（Session 0 服务无法枚举）。</summary>
    public const string ReportInputDevices = "report-input-devices";

    /// <summary>开启待机模式（PS5 式：睡眠转为离开模式，保持串流可达）。</summary>
    public const string EnableRestMode = "enable-rest-mode";

    /// <summary>关闭待机模式（恢复正常睡眠行为）。</summary>
    public const string DisableRestMode = "disable-rest-mode";

    /// <summary>上报待机会话状态（用户唤醒进桌面 = 会话结束，Away Mode 释放但武装保持）。</summary>
    public const string SetRestSession = "set-rest-session";

    /// <summary>上报串流状态（串流期间暂停 Away Mode，恢复音频供 Sunshine 抓取）。</summary>
    public const string SetStreaming = "set-streaming";

    /// <summary>探测串流环境（Sunshine / 虚拟显示设备 / 配置键），只读，不做任何变更。</summary>
    public const string ProbeStreamingEnv = "probe-streaming-env";

    /// <summary>分步执行一键启用或一键卸载（特权动作在服务端以 SYSTEM 完成）。</summary>
    public const string ApplyStreamingEnv = "apply-streaming-env";

    /// <summary>上报交互会话的显示器拓扑：Session 0 枚举不到屏幕，虚拟屏是否真的成为可用输出只能由客户端证实。</summary>
    public const string ReportStreamingTopology = "report-streaming-topology";
}
