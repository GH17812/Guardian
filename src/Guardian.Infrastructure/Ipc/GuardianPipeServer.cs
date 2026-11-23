using System.Collections.Concurrent;
using System.IO.Pipes;
using Guardian.Contracts.Protocol;
using Guardian.Infrastructure.Interop;

namespace Guardian.Infrastructure.Ipc;

/// <summary>
/// 命名管道服务端：接受多客户端连接，逐请求处理。
/// 安全模型：
/// - 连接建立受管道 ACL 约束（本机用户）；
/// - 协议版本不一致直接拒绝；
/// - 敏感命令（启用/解除保护、恢复、会话与设置变更）必须通过调用方身份校验。
/// </summary>
public sealed class GuardianPipeServer : IAsyncDisposable
{
    private static readonly HashSet<string> SensitiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        Commands.EnableProtection,
        Commands.DisableProtection,
        Commands.Recover,
        Commands.CreateSession,
        Commands.EndSession,
        Commands.UpdateSettings,
        Commands.ConfirmDisplayPrivacyCheck,
        Commands.ReportInputDevices,

        // 一键启用/卸载会以 SYSTEM 装驱动、改 Sunshine 配置、重启服务，属于最高权限动作。
        Commands.ApplyStreamingEnv,
    };

    private readonly string _pipeName;
    private readonly Func<RequestMessage, ResponseMessage> _handler;
    private readonly Func<uint, bool> _authorizer;
    private readonly Action<string> _logError;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Task> _clientTasks = new();
    private Task? _acceptLoop;
    private int _disposeStarted;

    public GuardianPipeServer(
        Func<RequestMessage, ResponseMessage> handler,
        Func<uint, bool> authorizer,
        Action<string> logError,
        string? pipeName = null)
    {
        _handler = handler;
        _authorizer = authorizer;
        _logError = logError;
        _pipeName = pipeName ?? ProtocolConstants.PipeName;
    }

    public void Start()
    {
        _acceptLoop = Task.Run(async () =>
        {
            try
            {
                await AcceptLoopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logError($"接收循环终止（致命）：{ex}");
            }
        });
    }

    private async Task AcceptLoopAsync()
    {
        var consecutiveFailures = 0;
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // 注意：创建监听实例必须位于 try 块内 —— 若创建失败抛异常逃逸，接收循环会静默死亡。
                server = PipeSecurityFactory.CreateServerStream(_pipeName);
                consecutiveFailures = 0;
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                var client = server;
                server = null;
                var task = Task.Run(() => HandleClientAsync(client));
                _clientTasks.Add(task);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // 已知行为：紧接上次接受后立即重建实例会偶发一次 Access denied，短延迟重试即恢复；
                // 仅在连续失败时告警，避免日志噪音。
                consecutiveFailures++;
                if (consecutiveFailures > 3)
                {
                    _logError($"管道监听连续失败 {consecutiveFailures} 次：{ex.Message}");
                }
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(150), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task HandleClientAsync(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            uint clientPid = 0;
            try
            {
                if (!NativeMethods.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out clientPid))
                {
                    return;
                }
            }
            catch
            {
                return;
            }

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var request = await PipeFraming.ReadRequestAsync(pipe, _cts.Token).ConfigureAwait(false);
                    if (request is null)
                    {
                        return; // 客户端断开
                    }

                    ResponseMessage response;
                    if (request.ProtocolVersion != ProtocolConstants.CurrentVersion)
                    {
                        response = ResponseMessage.Fail(request.Id, ErrorCodes.ProtocolMismatch,
                            $"协议版本不匹配：客户端 {request.ProtocolVersion}，服务端 {ProtocolConstants.CurrentVersion}");
                    }
                    else if (SensitiveCommands.Contains(request.Command) && !_authorizer(clientPid))
                    {
                        response = ResponseMessage.Fail(request.Id, ErrorCodes.Unauthorized, "调用方未通过身份校验，敏感操作已拒绝。");
                    }
                    else
                    {
                        response = SafeHandle(request);
                    }

                    await PipeFraming.WriteResponseAsync(pipe, response, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    return; // 连接异常，结束本次会话
                }
            }
        }
    }

    private ResponseMessage SafeHandle(RequestMessage request)
    {
        try
        {
            return _handler(request);
        }
        catch (Exception ex)
        {
            _logError($"命令 {request.Command} 处理异常：{ex}");
            return ResponseMessage.Fail(request.Id, ErrorCodes.InternalError, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // 幂等：宿主生命周期与手动释放可能同时触发（DI 容器 + GuardianHost finally）。
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
        {
            return;
        }
        _cts.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // 忽略
            }
        }
        try
        {
            await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false);
        }
        catch
        {
            // 忽略
        }
        _cts.Dispose();
    }
}
