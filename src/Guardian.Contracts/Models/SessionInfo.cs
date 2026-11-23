namespace Guardian.Contracts.Models;

/// <summary>工作会话。</summary>
public sealed class SessionInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public WorkloadType WorkloadType { get; set; }
    public SessionStatus Status { get; set; } = SessionStatus.Active;
    public ProtectionMode RequiredMode { get; set; }
    public SessionSource Source { get; set; } = SessionSource.Manual;
    public bool AllowAutoEnd { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public SessionEndReason? EndReason { get; set; }

    /// <summary>最长保护时长（分钟），null 表示不限制。</summary>
    public int? MaxDurationMinutes { get; set; }

    /// <summary>创建来源描述（例如进程名、外部 API 标识）。</summary>
    public string? SourceDetail { get; set; }
}
