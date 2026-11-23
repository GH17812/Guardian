using System.Text.Json;
using Guardian.Contracts;
using Guardian.Contracts.Protocol;
using Guardian.Infrastructure.Ipc;
using Xunit;

namespace Guardian.Integration.Tests;

/// <summary>真实命名管道端到端测试（服务端+客户端，进程内）。</summary>
public class PipeIntegrationTests : IAsyncLifetime
{
    private readonly string _pipeName = "Guardian.Test.Integration." + Guid.NewGuid().ToString("N");
    private GuardianPipeServer? _server;
    private readonly List<string> _serverErrors = new();

    public async Task InitializeAsync()
    {
        _server = new GuardianPipeServer(
            Handler,
            _ => true, // 测试中一律授权
            message => _serverErrors.Add(message),
            _pipeName);
        _server.Start();
        await Task.Delay(200); // 等待监听就绪
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    private static ResponseMessage Handler(RequestMessage request)
        => request.Command switch
        {
            Commands.Ping => ResponseMessage.Ok(request.Id, ProtocolJson.ToElement(new { pong = true })),
            Commands.GetStatus => ResponseMessage.Ok(request.Id, ProtocolJson.ToElement(new { state = "active" })),
            _ => ResponseMessage.Fail(request.Id, ErrorCodes.InvalidArgument, "unknown"),
        };

    [Fact]
    public async Task Ping_ReturnsPong()
    {
        var client = new GuardianPipeClient(_pipeName, TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(new RequestMessage { Command = Commands.Ping });

        Assert.True(response.Success);
        var data = ProtocolJson.FromElement<Dictionary<string, JsonElement>>(response.Data);
        Assert.True(data!["pong"].GetBoolean());
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var client = new GuardianPipeClient(_pipeName, TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(new RequestMessage { Command = "no-such-command" });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.InvalidArgument, response.ErrorCode);
    }

    [Fact]
    public async Task ServerUnavailable_Throws()
    {
        var client = new GuardianPipeClient("Guardian.Test.Nonexistent." + Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<Exception>(() => client.SendAsync(new RequestMessage { Command = Commands.Ping }));
    }
}

/// <summary>敏感命令授权路径测试：authorizer 返回 false 时必须拒绝。</summary>
public class PipeAuthorizationTests : IAsyncLifetime
{
    private readonly string _pipeName = "Guardian.Test.Auth." + Guid.NewGuid().ToString("N");
    private GuardianPipeServer? _server;

    public async Task InitializeAsync()
    {
        _server = new GuardianPipeServer(
            _ => ResponseMessage.Ok(_.Id),
            _ => false, // 一律拒绝
            _ => { },
            _pipeName);
        _server.Start();
        await Task.Delay(200);
    }

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task SensitiveCommand_Rejected_WhenNotAuthorized()
    {
        var client = new GuardianPipeClient(_pipeName, TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(new RequestMessage { Command = Commands.Recover });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.Unauthorized, response.ErrorCode);
    }

    [Fact]
    public async Task ReadOnlyCommand_Allowed_WithoutAuthorization()
    {
        var client = new GuardianPipeClient(_pipeName, TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(new RequestMessage { Command = Commands.GetStatus });

        Assert.True(response.Success);
    }

    [Fact]
    public async Task ProtocolMismatch_Rejected()
    {
        var client = new GuardianPipeClient(_pipeName, TimeSpan.FromSeconds(5));

        var response = await client.SendAsync(new RequestMessage
        {
            Command = Commands.GetStatus,
            ProtocolVersion = 999,
        });

        Assert.False(response.Success);
        Assert.Equal(ErrorCodes.ProtocolMismatch, response.ErrorCode);
    }
}
