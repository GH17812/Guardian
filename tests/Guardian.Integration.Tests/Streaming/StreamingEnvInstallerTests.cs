using Guardian.Windows.Streaming;
using Xunit;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// pnputil 失败码的可读性测试。重点不是文案漂亮，而是两条纪律：
/// 实测遇到过的码必须给出能照做的下一步；没验证过的码不许编造含义，只能给码 + 指向系统日志。
/// </summary>
public class StreamingEnvInstallerTests
{
    [Fact]
    public void AddDriverFailure_ExplainsUntrustedPublisher()
    {
        // 0xE0000242：新机在无网络环境下驱动入库失败的真实码（setupapi.dev.log 已证实成因）。
        var text = StreamingEnvInstaller.ExplainAddDriverFailure(unchecked((int)0xE0000242), @"C:\x\MttVDD.inf");

        Assert.Contains("发布者尚未受信", text);
        Assert.Contains("根证书", text);
        Assert.Contains("pnputil /add-driver", text);
    }

    [Fact]
    public void AddDriverFailure_DoesNotInventMeaningForUnknownCodes()
    {
        var text = StreamingEnvInstaller.ExplainAddDriverFailure(unchecked((int)0xE0000C06), @"C:\x\MttVDD.inf");

        Assert.Contains("0xE0000C06", text);
        Assert.Contains("setupapi.dev.log", text);
        Assert.DoesNotContain("发布者", text);
    }
}
