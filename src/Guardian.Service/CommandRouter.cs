using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Guardian.Core.Engine;
using Guardian.Storage.Repositories;

namespace Guardian.Service;

/// <summary>IPC 命令路由：把协议命令映射到保护引擎调用。</summary>
public sealed class CommandRouter
{
    private readonly ProtectionEngine _engine;
    private readonly LogRepository _logs;
    private readonly StreamingEnvOrchestrator _streaming;

    public CommandRouter(ProtectionEngine engine, LogRepository logs, StreamingEnvOrchestrator streaming)
    {
        _engine = engine;
        _logs = logs;
        _streaming = streaming;
    }

    public ResponseMessage Handle(RequestMessage request)
    {
        switch (request.Command)
        {
            case Commands.Ping:
                _engine.OnClientHeartbeat();
                return Ok(request, _engine.GetSnapshot());

            case Commands.GetStatus:
                return Ok(request, _engine.GetSnapshot());

            case Commands.EnableProtection:
            {
                var payload = Payload<EnableProtectionPayload>(request)
                    ?? new EnableProtectionPayload();
                return FromResult(request, _engine.EnableProtection(payload.Mode, payload.Source, payload.DisplayPrivacyCheckAcknowledged));
            }

            case Commands.DisableProtection:
            {
                var payload = Payload<DisableProtectionPayload>(request) ?? new DisableProtectionPayload();
                return FromResult(request, _engine.DisableProtection(payload.Source));
            }

            case Commands.Recover:
            {
                var payload = Payload<RecoverPayload>(request) ?? new RecoverPayload { Reason = RecoveryReason.UserRequest };
                return FromResult(request, _engine.Recover(payload.Reason, payload.Source, payload.Detail));
            }

            case Commands.CreateSession:
            {
                var payload = Payload<CreateSessionPayload>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少会话参数");
                }
                return FromResult(request, _engine.CreateSession(payload));
            }

            case Commands.EndSession:
            {
                var payload = Payload<EndSessionPayload>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少会话参数");
                }
                return FromResult(request, _engine.EndSession(payload.SessionId, payload.Reason));
            }

            case Commands.ListSessions:
                return Ok(request, _engine.ListSessions());

            case Commands.GetCapabilities:
                return Ok(request, _engine.GetCapabilities());

            case Commands.GetSettings:
                return Ok(request, _engine.GetSettings());

            case Commands.UpdateSettings:
            {
                var payload = Payload<GuardianSettings>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少设置参数");
                }
                return FromResult(request, _engine.UpdateSettings(payload));
            }

            case Commands.GetLogs:
            {
                var payload = Payload<GetLogsPayload>(request) ?? new GetLogsPayload();
                return Ok(request, _logs.GetRecent(payload.MaxCount));
            }

            case Commands.ConfirmDisplayPrivacyCheck:
            {
                var payload = Payload<ConfirmDisplayPrivacyCheckPayload>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少确认参数");
                }
                return FromResult(request, _engine.ConfirmDisplayPrivacyCheck(payload));
            }

            case Commands.ReportInputDevices:
            {
                var payload = Payload<List<InputDeviceCapability>>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少设备清单参数");
                }
                return FromResult(request, _engine.ReportInputDevices(payload));
            }

            case Commands.EnableRestMode:
            {
                var payload = Payload<RestModePayload>(request) ?? new RestModePayload();
                return FromResult(request, _engine.EnableRestMode(payload.Source));
            }

            case Commands.DisableRestMode:
            {
                var payload = Payload<RestModePayload>(request) ?? new RestModePayload();
                return FromResult(request, _engine.DisableRestMode(payload.Source));
            }

            case Commands.SetRestSession:
            {
                var payload = Payload<RestSessionPayload>(request) ?? new RestSessionPayload();
                return FromResult(request, _engine.SetRestSessionActive(payload.Active, payload.Source));
            }

            case Commands.SetStreaming:
            {
                var payload = Payload<StreamingPayload>(request) ?? new StreamingPayload();
                return FromResult(request, _engine.SetStreamingActive(payload.Active, payload.Source));
            }

            case Commands.ReportStreamingTopology:
            {
                var payload = Payload<StreamingTopologyPayload>(request);
                if (payload is null)
                {
                    return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "缺少拓扑上报参数");
                }
                return Ok(request, _streaming.ReportTopology(payload));
            }

            case Commands.ProbeStreamingEnv:
                return Ok(request, _streaming.Refresh());

            case Commands.ApplyStreamingEnv:
            {
                var payload = Payload<StreamingEnvPayload>(request) ?? new StreamingEnvPayload();
                return Ok(request, _streaming.Start(payload.Op, payload.Source));
            }

            default:
                return ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, $"未知命令：{request.Command}");
        }
    }

    private static T? Payload<T>(RequestMessage request)
        => ProtocolJson.FromElement<T>(request.Payload);

    private static ResponseMessage Ok<T>(RequestMessage request, T data)
        => ResponseMessage.Ok(request.Id, ProtocolJson.ToElement(data));

    private static ResponseMessage FromResult(RequestMessage request, EngineResult result)
        => result.Success
            ? ResponseMessage.Ok(request.Id)
            : ResponseMessage.Fail(request.Id, result.ErrorCode ?? ErrorCodes.InternalError, result.ErrorMessage ?? "未知错误");
}
