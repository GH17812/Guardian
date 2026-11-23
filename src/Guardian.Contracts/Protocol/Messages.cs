using System.Text.Json;

namespace Guardian.Contracts.Protocol;

/// <summary>IPC 请求帧。</summary>
public sealed class RequestMessage
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentVersion;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Command { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; }
    public string ClientProcessName { get; set; } = string.Empty;
    public JsonElement? Payload { get; set; }
}

/// <summary>IPC 响应帧。</summary>
public sealed class ResponseMessage
{
    public int ProtocolVersion { get; set; } = ProtocolConstants.CurrentVersion;
    public Guid RequestId { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public JsonElement? Data { get; set; }

    public static ResponseMessage Ok(Guid requestId, JsonElement? data = null) =>
        new() { RequestId = requestId, Success = true, Data = data };

    public static ResponseMessage Fail(Guid requestId, string code, string message) =>
        new() { RequestId = requestId, Success = false, ErrorCode = code, ErrorMessage = message };
}

/// <summary>错误码集合。</summary>
public static class ErrorCodes
{
    public const string Unauthorized = "unauthorized";
    public const string InvalidArgument = "invalid-argument";
    public const string InvalidState = "invalid-state";
    public const string ProtocolMismatch = "protocol-mismatch";
    public const string Unsupported = "unsupported";
    public const string InternalError = "internal-error";
    public const string ServiceUnavailable = "service-unavailable";
    public const string ConfirmationRequired = "confirmation-required";
}
