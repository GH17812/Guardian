using System.Text.Json;
using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Infrastructure.Ipc;

namespace Guardian.App.Services;

/// <summary>封装与后台服务之间的命名管道 IPC，所有方法返回可等待任务；异常统一包装为 <see cref="GuardianClientException"/>。</summary>
public sealed class GuardianClientService
{
    /// <summary>心跳 / 保活，返回状态快照。</summary>
    public Task<ProtectionSnapshot?> PingAsync()
        => SendAndGetAsync<ProtectionSnapshot>(Commands.Ping, null);

    /// <summary>获取完整保护状态快照。</summary>
    public Task<ProtectionSnapshot?> GetStatusAsync()
        => SendAndGetAsync<ProtectionSnapshot>(Commands.GetStatus, null);

    /// <summary>开启保护。</summary>
    public Task EnableProtectionAsync(ProtectionMode mode, string source, bool displayPrivacyCheckAcknowledged)
        => SendAsync(Commands.EnableProtection, new EnableProtectionPayload
        {
            Mode = mode,
            Source = source,
            DisplayPrivacyCheckAcknowledged = displayPrivacyCheckAcknowledged,
        });

    /// <summary>解除保护。</summary>
    public Task DisableProtectionAsync(string source)
        => SendAsync(Commands.DisableProtection, new DisableProtectionPayload { Source = source });

    /// <summary>紧急恢复。</summary>
    public Task RecoverAsync(RecoveryReason reason, RecoverySource source, string? detail)
        => SendAsync(Commands.Recover, new RecoverPayload { Reason = reason, Source = source, Detail = detail });

    /// <summary>列出全部会话。</summary>
    public async Task<List<SessionInfo>> ListSessionsAsync()
        => await SendAndGetAsync<List<SessionInfo>>(Commands.ListSessions, null) ?? new List<SessionInfo>();

    /// <summary>创建工作会话。</summary>
    public Task CreateSessionAsync(CreateSessionPayload payload)
        => SendAsync(Commands.CreateSession, payload);

    /// <summary>结束工作会话。</summary>
    public Task EndSessionAsync(Guid sessionId, SessionEndReason reason)
        => SendAsync(Commands.EndSession, new EndSessionPayload { SessionId = sessionId, Reason = reason });

    /// <summary>获取系统能力检测报告。</summary>
    public Task<CapabilityReport?> GetCapabilitiesAsync()
        => SendAndGetAsync<CapabilityReport>(Commands.GetCapabilities, null);

    /// <summary>获取服务端设置。</summary>
    public Task<GuardianSettings?> GetSettingsAsync()
        => SendAndGetAsync<GuardianSettings>(Commands.GetSettings, null);

    /// <summary>更新服务端设置。</summary>
    public Task UpdateSettingsAsync(GuardianSettings settings)
        => SendAsync(Commands.UpdateSettings, settings);

    /// <summary>获取最近操作日志。</summary>
    public async Task<List<OperationLogEntry>> GetLogsAsync(int max)
        => await SendAndGetAsync<List<OperationLogEntry>>(Commands.GetLogs, new GetLogsPayload { MaxCount = max })
            ?? new List<OperationLogEntry>();

    /// <summary>确认“关闭显示器后远程捕获是否正常”的验证结果。</summary>
    public Task ConfirmDisplayPrivacyCheckAsync(bool remoteCaptureOk, string? note)
        => SendAsync(Commands.ConfirmDisplayPrivacyCheck, new ConfirmDisplayPrivacyCheckPayload
        {
            RemoteCaptureOk = remoteCaptureOk,
            Note = note,
        });

    /// <summary>上报交互会话输入设备清单（Session 0 服务无法枚举）。</summary>
    public Task ReportInputDevicesAsync(List<InputDeviceCapability> devices)
        => SendAsync(Commands.ReportInputDevices, devices);

    /// <summary>开启待机模式（PS5 式：睡眠转为离开模式）。</summary>
    public Task EnableRestModeAsync(string source)
        => SendAsync(Commands.EnableRestMode, new RestModePayload { Source = source });

    /// <summary>关闭待机模式（恢复正常睡眠行为）。</summary>
    public Task DisableRestModeAsync(string source)
        => SendAsync(Commands.DisableRestMode, new RestModePayload { Source = source });

    /// <summary>上报待机会话状态（唤醒进桌面 = 会话结束，服务端释放 Away Mode）。</summary>
    public Task SetRestSessionAsync(bool active, string source)
        => SendAsync(Commands.SetRestSession, new RestSessionPayload { Active = active, Source = source });

    /// <summary>上报串流状态（串流期间服务端暂停 Away Mode 恢复音频）。</summary>
    public Task SetStreamingAsync(bool active, string source)
        => SendAsync(Commands.SetStreaming, new StreamingPayload { Active = active, Source = source });

    /// <summary>只读探测串流环境。</summary>
    public Task<StreamingEnvironmentProfile?> ProbeStreamingEnvAsync()
        => SendAndGetAsync<StreamingEnvironmentProfile>(Commands.ProbeStreamingEnv, null);

    /// <summary>发起一键启用/一键卸载。服务端立即返回 Busy 档案，进度靠后续轮询。</summary>
    public Task<StreamingEnvironmentProfile?> ApplyStreamingEnvAsync(StreamingEnvOp op, string source)
        => SendAndGetAsync<StreamingEnvironmentProfile>(Commands.ApplyStreamingEnv, new StreamingEnvPayload { Op = op, Source = source });

    /// <summary>
    /// 上报交互会话显示器拓扑，并拿回当前串流环境档案。
    /// 返回档案是故意的：服务端没有推送通道，客户端靠这一趟往返知道"是否轮到我夺回物理主显"。
    /// </summary>
    public Task<StreamingEnvironmentProfile?> ReportStreamingTopologyAsync(StreamingTopologyPayload payload)
        => SendAndGetAsync<StreamingEnvironmentProfile>(Commands.ReportStreamingTopology, payload);

    /// <summary>发送无返回数据的命令。</summary>
    private async Task SendAsync(string command, object? payload)
    {
        try
        {
            var client = new GuardianPipeClient();
            var response = await client.SendAsync(BuildRequest(command, payload));
            if (!response.Success)
            {
                throw new GuardianClientException(response.ErrorCode, response.ErrorMessage);
            }
        }
        catch (GuardianClientException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GuardianClientException(ErrorCodes.ServiceUnavailable, ex.Message);
        }
    }

    /// <summary>发送并反序列化返回数据的命令。</summary>
    private async Task<T?> SendAndGetAsync<T>(string command, object? payload)
    {
        try
        {
            var client = new GuardianPipeClient();
            var response = await client.SendAsync(BuildRequest(command, payload));
            if (!response.Success)
            {
                throw new GuardianClientException(response.ErrorCode, response.ErrorMessage);
            }
            return ProtocolJson.FromElement<T>(response.Data);
        }
        catch (GuardianClientException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GuardianClientException(ErrorCodes.ServiceUnavailable, ex.Message);
        }
    }

    private static RequestMessage BuildRequest(string command, object? payload)
    {
        var request = new RequestMessage { Command = command };
        if (payload is not null)
        {
            // 使用运行时类型序列化，避免以 object 序列化得到空对象。
            var json = JsonSerializer.Serialize(payload, payload.GetType(), ProtocolJson.Options);
            using var doc = JsonDocument.Parse(json);
            request.Payload = doc.RootElement.Clone();
        }
        return request;
    }
}
