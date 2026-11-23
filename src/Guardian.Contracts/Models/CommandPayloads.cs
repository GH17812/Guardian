using Guardian.Contracts.Models;

namespace Guardian.Contracts.Models;

/// <summary>开启保护命令参数。</summary>
public sealed class EnableProtectionPayload
{
    public ProtectionMode Mode { get; set; } = ProtectionMode.Privacy;

    /// <summary>发起来源描述（例如 "Guardian.App"）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>关闭显示器前的远程捕获验证确认（首次验证必需）。</summary>
    public bool DisplayPrivacyCheckAcknowledged { get; set; }
}

/// <summary>解除保护命令参数。</summary>
public sealed class DisableProtectionPayload
{
    public string Source { get; set; } = string.Empty;
}

/// <summary>恢复命令参数。</summary>
public sealed class RecoverPayload
{
    public RecoveryReason Reason { get; set; } = RecoveryReason.UserRequest;
    public RecoverySource Source { get; set; } = RecoverySource.Local;
    public string? Detail { get; set; }
}

/// <summary>创建会话命令参数。</summary>
public sealed class CreateSessionPayload
{
    public string Name { get; set; } = string.Empty;
    public WorkloadType WorkloadType { get; set; } = WorkloadType.Other;
    public ProtectionMode RequiredMode { get; set; } = ProtectionMode.Privacy;
    public SessionSource Source { get; set; } = SessionSource.Manual;
    public bool AllowAutoEnd { get; set; } = true;
    public int? MaxDurationMinutes { get; set; }
    public string? SourceDetail { get; set; }
}

/// <summary>结束会话命令参数。</summary>
public sealed class EndSessionPayload
{
    public Guid SessionId { get; set; }
    public SessionEndReason Reason { get; set; } = SessionEndReason.UserRequest;
}

/// <summary>查询日志参数。</summary>
public sealed class GetLogsPayload
{
    public int MaxCount { get; set; } = 500;
}

/// <summary>显示隐私验证确认参数。</summary>
public sealed class ConfirmDisplayPrivacyCheckPayload
{
    public bool RemoteCaptureOk { get; set; }
    public string? Note { get; set; }
}

/// <summary>待机模式命令参数。</summary>
public sealed class RestModePayload
{
    /// <summary>发起来源描述（例如 "Guardian.App" 或 "battery-low"）。</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>待机会话状态上报参数（客户端交互会话是会话状态的唯一知情者）。</summary>
public sealed class RestSessionPayload
{
    /// <summary>true=处于待机会话（屏幕应熄灭、系统可进入离开模式）。</summary>
    public bool Active { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>串流环境操作参数。</summary>
public sealed class StreamingEnvPayload
{
    public StreamingEnvOp Op { get; set; } = StreamingEnvOp.Enable;

    /// <summary>发起来源描述（例如 "Guardian.App"）。</summary>
    public string Source { get; set; } = string.Empty;
}

/// <summary>串流状态上报参数（串流检测依赖交互会话显示器拓扑，只能由客户端上报）。</summary>
public sealed class StreamingPayload
{
    /// <summary>true=串流进行中。</summary>
    public bool Active { get; set; }

    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// 交互会话显示器拓扑上报参数。只带计数与布尔，不带分辨率、设备名，更不带屏幕内容。
/// 一键启用写 ensure_only_display 之前要用它确认"虚拟屏确实是一块可用输出"。
/// </summary>
public sealed class StreamingTopologyPayload
{
    /// <summary>交互会话里枚举到了虚拟屏输出（已在桌面布局中，可当屏幕用）。</summary>
    public bool VirtualDisplayEnumerated { get; set; }

    /// <summary>虚拟适配器有 ACTIVE|ATTACHED 的显示路径（可被 Sunshine 激活，但可能还不在桌面里）。</summary>
    public bool VirtualDisplayPathActive { get; set; }

    /// <summary>交互会话里仍有活动的物理屏。</summary>
    public bool PhysicalDisplayActive { get; set; }

    /// <summary>当前活动显示器总数。</summary>
    public int ActiveMonitorCount { get; set; }
}
