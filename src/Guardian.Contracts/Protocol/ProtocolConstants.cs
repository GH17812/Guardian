namespace Guardian.Contracts.Protocol;

/// <summary>IPC 协议常量。协议版本用于握手校验，管道名与 Windows 服务名保持稳定。</summary>
public static class ProtocolConstants
{
    /// <summary>当前协议版本。服务端与客户端不一致时拒绝执行敏感命令。</summary>
    public const int CurrentVersion = 1;

    /// <summary>Named Pipe 名称。</summary>
    public const string PipeName = "Guardian.Control.v1";

    /// <summary>Windows 后台服务名称。</summary>
    public const string ServiceName = "GuardianService";

    /// <summary>单条消息最大字节数（含帧头）。</summary>
    public const int MaxMessageBytes = 1 * 1024 * 1024;

    /// <summary>客户端心跳（ping）间隔。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(3);

    /// <summary>服务端判定客户端丢失的阈值（连续无心跳）。</summary>
    public static readonly TimeSpan DefaultClientLostThreshold = TimeSpan.FromSeconds(15);
}
