namespace Guardian.App.Services;

/// <summary>IPC 调用失败的异常包装，携带服务端错误码与错误消息。</summary>
public sealed class GuardianClientException : Exception
{
    /// <summary>错误码（来自 <c>Guardian.Contracts.Protocol.ErrorCodes</c>）。</summary>
    public string? ErrorCode { get; }

    public GuardianClientException(string? errorCode, string? message)
        : base(message ?? "未知错误")
    {
        ErrorCode = errorCode;
    }
}
