namespace Guardian.Contracts.Models;

/// <summary>操作日志条目。</summary>
public sealed class OperationLogEntry
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public GuardianLogLevel Level { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? Detail { get; set; }
}

/// <summary>恢复步骤记录。</summary>
public sealed class RecoveryStepRecord
{
    public string Step { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>恢复记录。</summary>
public sealed class RecoveryRecord
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public RecoveryReason Reason { get; set; }
    public RecoverySource Source { get; set; }
    public bool Success { get; set; }
    public List<RecoveryStepRecord> Steps { get; set; } = new();
    public string? Detail { get; set; }
}
