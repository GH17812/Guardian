using Guardian.Windows.Streaming;
using Xunit;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// Sunshine 显示设备日志解析测试。样例行式取自本机 C:\Program Files\Sunshine\config\sunshine.log 的真实输出，
/// 目的是锁定 output_name 的唯一事实来源；解析错一个转义就可能把物理屏标识写进 ensure_only_display。
/// </summary>
public sealed class SunshineDisplayDeviceLogTests : IDisposable
{
    private readonly string _directory;

    public SunshineDisplayDeviceLogTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "guardian-sunshine-log-" + Guid.NewGuid().ToString("N"));
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
    public void ReadsFieldsFromRealLogShape()
    {
        var path = Write("block1.log", HeaderLine() + Block(VddEntry(), PanelEntry()));

        Assert.True(SunshineDisplayDeviceLog.TryReadLatest(path, out var devices, out var evidence));
        Assert.Equal(2, devices.Count);
        Assert.Contains("2 台", evidence);

        var vdd = devices.Single(d => d.DeviceId.StartsWith("{9acddf6d"));
        Assert.Equal(@"\\.\DISPLAY5", vdd.DisplayPath);
        Assert.Equal("VDD by MTT", vdd.FriendlyName);
        Assert.Equal("MTT", vdd.ManufacturerId);
        Assert.Equal("1337", vdd.ProductCode);
        Assert.False(vdd.Primary);

        var panel = devices.Single(d => d.DeviceId.StartsWith("{9e2806e4"));
        Assert.True(panel.Primary);
        Assert.Equal("AUO", panel.ManufacturerId);
    }

    [Fact]
    public void TakesTheLastBlockWhenSunshineLoggedTwice()
    {
        var path = Write(
            "twice.log",
            HeaderLine() + Block(VddEntry(deviceId: "{old-old-old}")) + "\n" + HeaderLine() + Block(VddEntry()));

        Assert.True(SunshineDisplayDeviceLog.TryReadLatest(path, out var devices, out _));
        var only = Assert.Single(devices);
        Assert.StartsWith("{9acddf6d", only.DeviceId);
    }

    [Fact]
    public void FindVirtual_PicksUniqueVirtualCandidate()
    {
        var path = Write("pick.log", HeaderLine() + Block(VddEntry(), PanelEntry()));
        Assert.True(SunshineDisplayDeviceLog.TryReadLatest(path, out var devices, out _));

        var virtualDevice = SunshineDisplayDeviceLog.FindVirtual(devices, out var evidence);

        Assert.NotNull(virtualDevice);
        Assert.StartsWith("{9acddf6d", virtualDevice!.DeviceId);
        Assert.Contains("候选", evidence);
    }

    [Fact]
    public void FindVirtual_IgnoresThirdPartyVirtualAdapters()
    {
        // 新机实测场景：机器上装着 ToDesk，它的适配器也叫 "Virtual Display ..."。
        // 泛用词判定会把它当成本功能的虚拟屏，于是 Guardian 以为驱动已装、跳过安装。
        var devices = new List<SunshineDisplayDevice>
        {
            new() { DeviceId = "{todesk-0001}", FriendlyName = "Todesk Virtual Display Adapter", ManufacturerId = "TDS", DisplayPath = @"\.\DISPLAY2" },
            new() { DeviceId = "{panel}", FriendlyName = string.Empty, ManufacturerId = "AUO", DisplayPath = @"\.\DISPLAY1" },
        };

        Assert.Null(SunshineDisplayDeviceLog.FindVirtual(devices, out var evidence));
        Assert.Contains("没有可判定为虚拟屏", evidence);
    }

    [Fact]
    public void FindVirtual_RefusesAmbiguousCandidates()
    {
        var devices = new List<SunshineDisplayDevice>
        {
            new() { DeviceId = "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}", FriendlyName = "VDD by MTT", ManufacturerId = "MTT", ProductCode = "1337" },
            new() { DeviceId = "{second-vdd}", FriendlyName = "Virtual Display Driver B", ManufacturerId = "ZZZ" },
        };

        Assert.Null(SunshineDisplayDeviceLog.FindVirtual(devices, out var evidence));
        Assert.Contains("无法确定唯一", evidence);
    }

    [Fact]
    public void FindVirtual_ReportsWhatWasSeenWhenNothingMatches()
    {
        var devices = new List<SunshineDisplayDevice>
        {
            new() { DeviceId = "{panel}", FriendlyName = string.Empty, ManufacturerId = "AUO", DisplayPath = @"\\.\DISPLAY1" },
        };

        Assert.Null(SunshineDisplayDeviceLog.FindVirtual(devices, out var evidence));
        Assert.Contains("没有可判定为虚拟屏", evidence);
        Assert.Contains("AUO", evidence);
    }

    [Fact]
    public void MissingLog_ExplainsItself()
    {
        Assert.False(SunshineDisplayDeviceLog.TryReadLatest(Path.Combine(_directory, "nope.log"), out _, out var evidence));
        Assert.Contains("未找到 Sunshine 日志", evidence);
    }

    [Fact]
    public void LogWithoutDeviceList_IsNotTreatedAsEvidence()
    {
        var path = Write("quiet.log", "[2026-11-19 15:25:01.419]: Info: Sunshine version: 2025.924.154138\n");

        Assert.False(SunshineDisplayDeviceLog.TryReadLatest(path, out _, out var evidence));
        Assert.Contains("没有显示设备清单", evidence);
    }

    [Fact]
    public void TruncatedBlock_IsRejected()
    {
        // Sunshine 边写边读时会碰到这种半截块，绝不能拿半截 JSON 当依据。
        var path = Write("half.log", HeaderLine() + "[\n  {\n    \"device_id\": \"{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}\",\n");

        Assert.False(SunshineDisplayDeviceLog.TryReadLatest(path, out _, out var evidence));
        Assert.Contains("不完整", evidence);
    }

    private static string HeaderLine() => "[2026-11-19 15:25:01.424]: Info: Currently available display devices:\n";

    private static string Block(params string[] entries)
        => "[\n" + string.Join(",\n", entries) + "\n]\n";

    private static string VddEntry(string deviceId = "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}")
        => $$"""
             {
               "device_id": "{{deviceId}}",
               "display_name": "\\\\.\\DISPLAY5",
               "edid": {
                 "manufacturer_id": "MTT",
                 "product_code": "1337",
                 "serial_number": 518463207
               },
               "friendly_name": "VDD by MTT",
               "info": {
                 "primary": false,
                 "resolution": { "height": 2160, "width": 3840 }
               }
             }
             """;

    private static string PanelEntry()
        => """
             {
               "device_id": "{9e2806e4-4ed6-53fa-ac10-f94388781ad0}",
               "display_name": "\\\\.\\DISPLAY1",
               "edid": {
                 "manufacturer_id": "AUO",
                 "product_code": "F1A7",
                 "serial_number": 0
               },
               "friendly_name": "",
               "info": {
                 "primary": true
               }
             }
             """;

    private string Write(string name, string content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
