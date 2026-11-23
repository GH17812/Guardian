using Guardian.Windows.Streaming;
using Xunit;

namespace Guardian.Integration.Tests.Streaming;

/// <summary>
/// Sunshine 配置白名单编辑测试。核心承诺有两条：
/// 写只动我们接管的键；还原不得覆盖用户在启用之后自己改的东西。
/// </summary>
public class SunshineConfigEditorTests : IDisposable
{
    private const string OutputGuid = "{9acddf6d-43cc-576e-9aff-0c5fc80b4cc8}";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "guardian-cfg-" + Guid.NewGuid().ToString("N"));
    private readonly string _path;

    public SunshineConfigEditorTests()
    {
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "sunshine.conf");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
            // 临时目录清不掉不影响结论。
        }
    }

    private void WriteOriginal(string content) => File.WriteAllText(_path, content, new System.Text.UTF8Encoding(false));

    private string Read() => File.ReadAllText(_path);

    [Fact]
    public void ApplyEnabled_SetsManagedKeysAndKeepsUserLines()
    {
        WriteOriginal("global_prep_cmd = [{\"do\":\"\"}]\r\nlocale = zh\r\nupnp = enabled\r\n");
        var editor = new SunshineConfigEditor(_path);

        Assert.True(editor.TryApplyEnabled(OutputGuid, out var evidence));
        var values = editor.ReadValues();

        Assert.Equal(OutputGuid, values[SunshineConfigEditor.KeyOutputName]);
        Assert.Equal("ensure_only_display", values[SunshineConfigEditor.KeyConfiguration]);
        Assert.Equal("auto", values[SunshineConfigEditor.KeyResolution]);

        // 用户的键必须逐行还在，包括带 = 的 JSON 值。
        Assert.Contains("global_prep_cmd = [{\"do\":\"\"}]", Read());
        Assert.Contains("upnp = enabled", Read());
        Assert.DoesNotContain("\r\n\r\n", Read());
        Assert.False(string.IsNullOrWhiteSpace(evidence));
    }

    [Fact]
    public void ApplyEnabled_IsIdempotent()
    {
        WriteOriginal("locale = zh\r\n");
        var editor = new SunshineConfigEditor(_path);

        Assert.True(editor.TryApplyEnabled(OutputGuid, out _));
        var after = Read();

        Assert.True(editor.TryApplyEnabled(OutputGuid, out var evidence));
        Assert.Equal(after, Read());
        Assert.Contains("未改动文件", evidence);
    }

    [Fact]
    public void Revert_PreservesSettingsUserAddedAfterEnable()
    {
        WriteOriginal("locale = zh\r\n");
        var editor = new SunshineConfigEditor(_path);
        Assert.True(editor.TryApplyEnabled(OutputGuid, out _));

        // 用户在 Sunshine Web UI 里加了设置（Web UI 保存会重写整文件，这里模拟成追加一行）。
        File.AppendAllText(_path, "gamepad = always\r\n");

        Assert.True(editor.TryRevert(out _));
        var content = Read();
        var values = editor.ReadValues();

        Assert.False(values.ContainsKey(SunshineConfigEditor.KeyOutputName));
        Assert.False(values.ContainsKey(SunshineConfigEditor.KeyConfiguration));
        Assert.Contains("gamepad = always", content);
        Assert.Contains("locale = zh", content);
    }

    [Fact]
    public void Revert_RestoresValueUserAlreadyHad_BeforeWeTookOver()
    {
        WriteOriginal("dd_resolution_option = manual\r\nlocale = zh\r\n");
        var editor = new SunshineConfigEditor(_path);

        Assert.True(editor.TryApplyEnabled(OutputGuid, out _));
        Assert.Equal("auto", editor.ReadValues()[SunshineConfigEditor.KeyResolution]);

        Assert.True(editor.TryRevert(out _));

        // 用户原本就有的键应恢复原值，而不是被删掉。
        Assert.Equal("manual", editor.ReadValues()[SunshineConfigEditor.KeyResolution]);
    }

    [Fact]
    public void Revert_IgnoresBackupThatIsAlreadyTakeoverForm()
    {
        // 本机实测踩到的场景：备份是在"已经接管"状态下拍的（里面就带着 ensure_only_display），
        // 拿它当基线还原等于没恢复。这种备份必须视为不可用，回到"删除我们接管的键"。
        var takeover = $"locale = zh\r\nupnp = enabled\r\noutput_name = {OutputGuid}\r\n"
            + "dd_configuration_option = ensure_only_display\r\ndd_resolution_option = auto\r\n";
        WriteOriginal(takeover);
        File.WriteAllText(_path + ".guardian-orig", takeover);
        var editor = new SunshineConfigEditor(_path);

        Assert.True(editor.TryRevert(out var evidence));
        var values = editor.ReadValues();

        Assert.False(values.ContainsKey(SunshineConfigEditor.KeyOutputName));
        Assert.False(values.ContainsKey(SunshineConfigEditor.KeyConfiguration));
        Assert.Equal("zh", values["locale"]);
        Assert.Equal("enabled", values["upnp"]);
        Assert.Contains("无接管配置", evidence);
    }

    [Fact]
    public void ApplyEnabled_DoesNotSnapshotAnAlreadyTakenOverFile()
    {
        // 已经接管过一次（用户手改或上一轮遗留）时再写，不该把接管状态存成"接管前基线"。
        WriteOriginal($"locale = zh\r\noutput_name = {OutputGuid}\r\ndd_configuration_option = ensure_only_display\r\ndd_resolution_option = manual\r\n");
        var editor = new SunshineConfigEditor(_path);

        Assert.True(editor.TryApplyEnabled(OutputGuid, out _));

        Assert.False(File.Exists(_path + ".guardian-orig"));
        Assert.Equal("auto", editor.ReadValues()[SunshineConfigEditor.KeyResolution]);
    }

    [Fact]
    public void ApplyEnabled_RejectsMalformedOutputName()
    {
        WriteOriginal("locale = zh\r\n");
        var editor = new SunshineConfigEditor(_path);
        var before = Read();

        Assert.False(editor.TryApplyEnabled("", out var emptyEvidence));
        Assert.False(editor.TryApplyEnabled("VDD by MTT", out _));
        Assert.Equal(before, Read());
        Assert.Contains("拒绝写入", emptyEvidence);
    }

    [Fact]
    public void IsEnabledForm_ReflectsFileContent()
    {
        WriteOriginal("locale = zh\r\n");
        var editor = new SunshineConfigEditor(_path);

        Assert.False(editor.IsEnabledForm(editor.ReadValues(), OutputGuid));
        Assert.True(editor.TryApplyEnabled(OutputGuid, out _));
        Assert.True(editor.IsEnabledForm(editor.ReadValues(), OutputGuid));
        Assert.False(editor.IsEnabledForm(editor.ReadValues(), "{other-guid-00000000}"));
    }

    [Fact]
    public void MissingFile_ReportsWithoutThrowing()
    {
        var editor = new SunshineConfigEditor(Path.Combine(_dir, "nope.conf"));

        Assert.False(editor.Exists);
        Assert.Empty(editor.ReadValues());
        Assert.True(editor.TryRevert(out var evidence));
        Assert.Contains("不存在", evidence);
    }
}
