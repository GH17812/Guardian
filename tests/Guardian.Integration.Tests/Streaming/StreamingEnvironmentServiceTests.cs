using Guardian.Contracts.Models;
using Guardian.Windows.Streaming;
using Xunit;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// 真机适配层测试：探测结论必须与本机实测事实一致，且"客户端上报过期"绝不能被当成"虚拟屏已枚举"。
/// </summary>
public sealed class StreamingEnvironmentServiceTests : IDisposable
{
    private readonly string _directory;

    public StreamingEnvironmentServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "guardian-effective-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    [Fact]
    public void EffectiveConfig_TakesTheLastValuePerKey()
    {
        var path = Write("sunshine.log", """
            [2026-11-19 15:25:01.419]: Info: Sunshine version: x
            [2026-11-19 15:25:01.419]: Info: config: 'output_name' = {stale-stale}
            [2026-11-19 15:25:01.419]: Info: config: 'dd_configuration_option' = disabled
            [2026-11-19 16:02:11.001]: Info: config: 'output_name' = {9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}
            [2026-11-19 16:02:11.001]: Info: config: 'dd_configuration_option' = ensure_only_display
            """);

        Assert.True(SunshineEffectiveConfig.TryRead(path, out var values, out var evidence));
        Assert.Equal("ensure_only_display", values["dd_configuration_option"]);
        Assert.Equal("{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}", values["output_name"]);
        Assert.Contains("生效配置键", evidence);
    }

    [Fact]
    public void EffectiveConfig_IgnoresValuesFromAnOlderStartupBlock()
    {
        // 恢复后重启：新启动块里没有接管键，绝不能回退去读上一块的旧值
        // （实测：这样会把"已还原"误判成 PartialFailure）。
        var path = Write("two-blocks.log", """
            [2026-11-19 15:25:01.419]: Info: Sunshine version: x
            [2026-11-19 15:25:01.419]: Info: config: 'output_name' = {9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}
            [2026-11-19 15:25:01.419]: Info: config: 'dd_configuration_option' = ensure_only_display
            [2026-11-19 16:02:11.001]: Info: Sunshine version: x
            [2026-11-19 16:02:11.001]: Info: config: 'locale' = zh
            """);

        Assert.True(SunshineEffectiveConfig.TryRead(path, out var values, out var evidence), evidence);
        Assert.Contains("启动块", evidence);

        // 上一块的接管键绝不能漏进来：这才是"已还原"不被误判成 PartialFailure 的关键。
        Assert.DoesNotContain("output_name", values.Keys);
        Assert.DoesNotContain("dd_configuration_option", values.Keys);
        Assert.Equal("zh", values["locale"]);
    }

    [Fact]
    public void EffectiveConfig_ReportsWhenTheLogHasNoConfigLines()
    {
        var path = Write("quiet.log", "[2026-11-19 15:25:01.419]: Info: Sunshine version: 2025.924.154138\n");

        Assert.False(SunshineEffectiveConfig.TryRead(path, out _, out var evidence));
        Assert.Contains("没有 config: 行", evidence);
    }

    [Fact]
    public void Probe_UsesLiveFactsAndFreshTopologyReport()
    {
        var service = new StreamingEnvironmentService(() => new StreamingTopologyFact
        {
            VirtualDisplayEnumerated = true,
            PhysicalDisplayActive = true,
            ActiveMonitorCount = 2,
            ReportedAt = DateTimeOffset.Now,
        });

        var profile = service.Probe();

        Assert.True(profile.Sunshine.Installed, profile.Sunshine.InstallSource);
        Assert.NotEmpty(profile.Sunshine.Version);
        Assert.True(profile.VirtualDisplay.DevicePresent, profile.VirtualDisplay.InstanceId);
        Assert.StartsWith("ROOT", profile.VirtualDisplay.InstanceId);

        // 断言的是探测逻辑的自洽，不是机器此刻的形态：
        // 说"已枚举命中"就必须拿得出 output_name，且文件形态与运行形态要么一致要么标记漂移。
        if (profile.VirtualDisplay.Enumerated)
        {
            Assert.NotNull(profile.VirtualDisplay.OutputGuid);
            Assert.StartsWith("{", profile.VirtualDisplay.OutputGuid!);
        }

        Assert.Equal(profile.Sunshine.Configured != profile.Sunshine.EffectiveConfigured, profile.Sunshine.ConfigDrifted);

        // 新鲜上报必须被采信：客户端说物理屏活动、枚举到虚拟屏，档案就要照实反映。
        Assert.True(profile.VirtualDisplay.PhysicalDisplayActive);
    }

    [Fact]
    public void Probe_StaleTopologyReport_IsReportedAsUnknownNotAsEnumerated()
    {
        var service = new StreamingEnvironmentService(
            () => new StreamingTopologyFact
            {
                VirtualDisplayEnumerated = true,
                PhysicalDisplayActive = true,
                ReportedAt = DateTimeOffset.Now - TimeSpan.FromMinutes(5),
            },
            topologyFreshness: TimeSpan.FromSeconds(30));

        var profile = service.Probe();

        // 过期上报既不能被当成"已枚举"，也不能被当成"物理屏正常"——只能是未知。
        Assert.False(profile.VirtualDisplay.Enumerated);
        Assert.Null(profile.VirtualDisplay.PhysicalDisplayActive);
    }

    [Fact]
    public void Probe_WithoutAnyClientReport_KeepsPhysicalStateUnknown()
    {
        var service = new StreamingEnvironmentService(() => null);

        var profile = service.Probe();

        Assert.False(profile.VirtualDisplay.Enumerated);
        Assert.Null(profile.VirtualDisplay.PhysicalDisplayActive);
    }

    [Fact]
    public async Task RestorePhysicalDisplay_WithoutInteractiveSession_ReportsHonestly()
    {
        var service = new StreamingEnvironmentService(() => null);

        var outcome = await service.RestorePhysicalDisplayAsync(CancellationToken.None);

        Assert.False(outcome.Ok);
        Assert.Contains("未接入交互会话", outcome.Evidence);
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
