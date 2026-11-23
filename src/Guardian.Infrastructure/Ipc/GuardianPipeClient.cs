using System.Diagnostics;
using System.IO.Pipes;
using Guardian.Contracts.Protocol;

namespace Guardian.Infrastructure.Ipc;

/// <summary>
/// 命名管道客户端：同步/异步发送请求并等待响应。
/// 客户端不上传任何凭据；服务端通过进程身份校验授权。
/// </summary>
public sealed class GuardianPipeClient
{
    private readonly string _pipeName;
    private readonly TimeSpan _connectTimeout;

    public GuardianPipeClient(string? pipeName = null, TimeSpan? connectTimeout = null)
    {
        _pipeName = pipeName ?? ProtocolConstants.PipeName;
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(3);
    }

    public bool IsServerAvailable()
    {
        try
        {
            using var pipe = Connect();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken ct = default)
    {
        using var pipe = await ConnectAsync(ct).ConfigureAwait(false);
        request.ClientProcessName = Process.GetCurrentProcess().ProcessName + ".exe";
        request.SentAt = DateTimeOffset.Now;
        await PipeFraming.WriteRequestAsync(pipe, request, ct).ConfigureAwait(false);
        return await PipeFraming.ReadResponseAsync(pipe, ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("服务端未返回响应");
    }

    public ResponseMessage Send(RequestMessage request, TimeSpan? timeout = null)
        => SendAsync(request, new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10)).Token)
            .GetAwaiter().GetResult();

    private NamedPipeClientStream Connect()
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        pipe.Connect((int)_connectTimeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Byte;
        return pipe;
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(_connectTimeout, ct).ConfigureAwait(false);
        pipe.ReadMode = PipeTransmissionMode.Byte;
        return pipe;
    }
}
