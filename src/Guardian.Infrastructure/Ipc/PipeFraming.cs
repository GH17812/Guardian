using System.Text;
using Guardian.Contracts.Protocol;

namespace Guardian.Infrastructure.Ipc;

/// <summary>管道消息分帧：4 字节小端长度前缀 + UTF-8 JSON。</summary>
public static class PipeFraming
{
    public static async Task<RequestMessage?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var body = await ReadBodyAsync(stream, ct);
        if (body is null)
        {
            return null; // 对端关闭
        }
        return ProtocolJson.Deserialize<RequestMessage>(Encoding.UTF8.GetString(body))
            ?? throw new InvalidDataException("请求反序列化为空");
    }

    public static async Task WriteResponseAsync(Stream stream, ResponseMessage response, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(response));
        await WriteBodyAsync(stream, body, ct);
    }

    public static async Task WriteRequestAsync(Stream stream, RequestMessage request, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(ProtocolJson.Serialize(request));
        await WriteBodyAsync(stream, body, ct);
    }

    public static async Task<ResponseMessage?> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        var body = await ReadBodyAsync(stream, ct);
        if (body is null)
        {
            return null;
        }
        return ProtocolJson.Deserialize<ResponseMessage>(Encoding.UTF8.GetString(body))
            ?? throw new InvalidDataException("响应反序列化为空");
    }

    private static async Task WriteBodyAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        var header = BitConverter.GetBytes(body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task<byte[]?> ReadBodyAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct))
        {
            return null; // 对端关闭
        }

        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > ProtocolConstants.MaxMessageBytes)
        {
            throw new InvalidDataException($"非法消息长度：{length}");
        }

        var body = new byte[length];
        await ReadExactAsync(stream, body, ct);
        return body;
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0)
            {
                return offset == 0 ? false : throw new EndOfStreamException("连接中途断开");
            }
            offset += read;
        }
        return true;
    }
}
