using System.Text;

namespace Guardian.Windows.Streaming;

/// <summary>
/// Sunshine 配置文件的白名单编辑器：只增删改 Guardian 接管的键，其余行（含注释与用户自加设置）逐行原样保留。
/// 还原取的是"首次接管前的原始值"而不是整文件回滚 —— Sunshine Web UI 保存时会重写整个文件，
/// 拿备份直接覆盖会连带抹掉用户在启用之后自己改的编码器、分辨率等设置。
/// </summary>
public sealed class SunshineConfigEditor
{
    public const string KeyOutputName = "output_name";
    public const string KeyConfiguration = "dd_configuration_option";
    public const string KeyResolution = "dd_resolution_option";

    private const string EnsureOnlyDisplay = "ensure_only_display";

    /// <summary>
    /// 只激活并设主显、不停用其它屏的取值。接管的第一阶段必须用它：
    /// 实测虚拟路径存在但不在桌面布局里时，直接写 ensure_only_display 会让本机没有可用输出。
    /// </summary>
    private const string EnsurePrimary = "ensure_primary";

    private const string ResolutionAuto = "auto";

    private static readonly object IoLock = new();

    private readonly string _path;
    private readonly string _pristinePath;

    public SunshineConfigEditor(string configPath)
    {
        _path = Path.GetFullPath(configPath);
        _pristinePath = _path + ".guardian-orig";
    }

    /// <summary>Guardian 会写的键。写死在此，避免调用方塞进意外配置。</summary>
    public static IReadOnlyList<string> ManagedKeys { get; } = new[] { KeyOutputName, KeyConfiguration, KeyResolution };

    public bool Exists => File.Exists(_path);

    public IReadOnlyDictionary<string, string> ReadValues()
    {
        lock (IoLock)
        {
            return ReadValuesUnlocked();
        }
    }

    /// <summary>写入"虚拟屏接管拓扑"形态。outputGuid 非法时拒绝，不写出会让系统没有可用输出的配置。</summary>
    public bool TryApplyEnabled(string outputGuid, out string evidence)
    {
        if (!IsPlausibleOutputName(outputGuid))
        {
            evidence = $"拒绝写入：output_name 不是形如 {{...}} 的显示标识（收到“{outputGuid}”）。";
            return false;
        }

        return TryApply(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyOutputName] = outputGuid.Trim(),
            [KeyConfiguration] = EnsureOnlyDisplay,
            [KeyResolution] = ResolutionAuto,
        }, out evidence);
    }

    /// <summary>写入"只激活虚拟屏、不停物理屏"的第一阶段形态。</summary>
    public bool TryApplyActivating(string outputGuid, out string evidence)
    {
        if (!IsPlausibleOutputName(outputGuid))
        {
            evidence = $"拒绝写入：output_name 不是形如 {{...}} 的显示标识（收到“{outputGuid}”）。";
            return false;
        }

        return TryApply(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyOutputName] = outputGuid.Trim(),
            [KeyConfiguration] = EnsurePrimary,
            [KeyResolution] = ResolutionAuto,
        }, out evidence);
    }

    /// <summary>退回"不接管显示拓扑"形态：有原始值就还原，原始文件里没这个键就删除我们写的行。</summary>
    public bool TryRevert(out string evidence)
    {
        lock (IoLock)
        {
            if (!Exists)
            {
                evidence = $"配置文件不存在，无需还原：{_path}";
                return true;
            }

            var pristine = File.Exists(_pristinePath)
                ? ParseLines(File.ReadAllText(_pristinePath))
                : null;

            // 备份本身已是接管形态 = 它是在接管状态下拍的快照，不是"接管前"基线：
            // 拿它还原等于把 ensure_only_display 再写回去，"一键恢复"就变成了没恢复。
            // 本机实测就踩到了：18:12 那次还原前文件已是接管形态，备份里就带着三个接管键。
            if (pristine is not null && IsEnabledForm(ValuesOf(pristine)))
            {
                pristine = null;
            }

            var desired = new Dictionary<string, string?>(StringComparer.Ordinal);
            var planned = new List<string>();
            foreach (var key in ManagedKeys)
            {
                var original = pristine?.FirstOrDefault(l => l.IsKey && string.Equals(l.Key, key, StringComparison.Ordinal))?.Value;
                desired[key] = original;
                planned.Add($"{key}={(original ?? "<删除>")}");
            }

            if (!TryApplyCore(desired, out var detail))
            {
                evidence = detail;
                return false;
            }

            evidence = pristine is null
                ? $"按「无接管配置」形态清理：{string.Join("，", planned)}（没有可用的接管前备份，删除我们接管的键即可回到 Sunshine 默认不接管拓扑）。"
                : $"已按接管前的原始值还原：{string.Join("，", planned)}。备份保留于 {Path.GetFileName(_pristinePath)}。";
            return true;
        }
    }

    /// <summary>当前文件是否已是启用形态（用于探测，不写盘）。</summary>
    public bool IsEnabledForm(IReadOnlyDictionary<string, string> values, string outputGuid)
        => values.TryGetValue(KeyConfiguration, out var mode)
            && mode.Equals(EnsureOnlyDisplay, StringComparison.OrdinalIgnoreCase)
            && values.TryGetValue(KeyOutputName, out var name)
            && name.Trim().Equals(outputGuid.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>文件是否处于"接管拓扑"形态：不要求与当前某台设备精确对应，只看键值形态。</summary>
    public static bool IsEnabledForm(IReadOnlyDictionary<string, string> values)
        => values.TryGetValue(KeyConfiguration, out var mode)
            && mode.Equals(EnsureOnlyDisplay, StringComparison.OrdinalIgnoreCase)
            && values.TryGetValue(KeyOutputName, out var name)
            && IsPlausibleOutputName(name);

    /// <summary>目标值本身：写入形态与"是否已生效"判定共用，避免两处拼写漂移。</summary>
    public const string EnsureOnlyDisplayValue = EnsureOnlyDisplay;

    /// <summary>第一阶段（只激活）的目标值。</summary>
    public const string EnsurePrimaryValue = EnsurePrimary;

    /// <summary>文件是否处于第一阶段形态（ensure_primary）。</summary>
    public static bool IsActivatingForm(IReadOnlyDictionary<string, string> values)
        => values.TryGetValue(KeyConfiguration, out var mode)
            && mode.Equals(EnsurePrimary, StringComparison.OrdinalIgnoreCase)
            && values.TryGetValue(KeyOutputName, out var name)
            && IsPlausibleOutputName(name);

    /// <summary>文件是否处于"不接管拓扑"形态（两个接管形态都不是）。</summary>
    public static bool IsRevertedForm(IReadOnlyDictionary<string, string> values)
        => !IsEnabledForm(values) && !IsActivatingForm(values);

    /// <summary>
    /// output_name 必须是形如 {…} 的显示标识。这是最后一道闸：写错值会让 Sunshine 在客户端连上时
    /// 停掉物理屏却抓不到任何输出，本机将没有可用画面。
    /// </summary>
    public static bool IsPlausibleOutputName(string? value)
    {
        var v = value?.Trim() ?? string.Empty;
        return v.Length >= 34 && v.StartsWith('{') && v.EndsWith('}');
    }

    private bool TryApply(Dictionary<string, string> desired, out string evidence)
    {
        var withNulls = desired.ToDictionary(kv => kv.Key, kv => (string?)kv.Value, StringComparer.Ordinal);
        return TryApplyCore(withNulls, out evidence);
    }

    private bool TryApplyCore(Dictionary<string, string?> desired, out string evidence)
    {
        lock (IoLock)
        {
            var originalText = Exists ? File.ReadAllText(_path) : string.Empty;
            var lines = ParseLines(originalText);
            var newline = originalText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            var applied = new List<string>();
            foreach (var pair in desired)
            {
                var index = lines.FindIndex(l => l.IsKey && string.Equals(l.Key, pair.Key, StringComparison.Ordinal));

                if (pair.Value == null)
                {
                    if (index >= 0)
                    {
                        lines.RemoveAt(index);
                        applied.Add($"{pair.Key} 已删除");
                    }

                    continue;
                }

                if (index >= 0)
                {
                    if (string.Equals(lines[index].Value, pair.Value, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    applied.Add($"{pair.Key}: {lines[index].Value} → {pair.Value}");
                    lines[index] = Line.Setting(pair.Key, pair.Value);
                    continue;
                }

                // 插在文件末尾空行之前，避免每写一次就多出一个空行。
                var insertAt = lines.Count > 0 && lines[^1].Text.Length == 0 ? lines.Count - 1 : lines.Count;
                lines.Insert(insertAt, Line.Setting(pair.Key, pair.Value));
                applied.Add($"{pair.Key} = {pair.Value}（新增行）");
            }

            if (applied.Count == 0)
            {
                evidence = "配置已处于目标形态，未改动文件。";
                return true;
            }

            // 基线只在"尚未接管"时拍：接管状态下拍的快照当基线用，还原等于把接管写回去。
            var wasTakeover = IsEnabledForm(ValuesOf(ParseLines(originalText)));

            if (Exists && !File.Exists(_pristinePath) && !wasTakeover)
            {
                try
                {
                    File.Copy(_path, _pristinePath);
                }
                catch (Exception ex)
                {
                    evidence = "无法创建接管前备份，已放弃写入以免不可回退：" + ex.Message;
                    return false;
                }
            }

            try
            {
                var text = string.Join(newline, lines.Select(l => l.Text));
                if (!originalText.EndsWith('\n') && text.Length > 0)
                {
                    text += newline;
                }

                File.WriteAllText(_path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
            catch (Exception ex)
            {
                evidence = $"写入失败（文件可能被 Sunshine 占用）：{ex.Message}";
                return false;
            }

            evidence = $"已写入 {Path.GetFileName(_path)}：{string.Join("，", applied)}"
                + (File.Exists(_pristinePath) ? $"；接管前原始文件在 {Path.GetFileName(_pristinePath)}。" : "。");
            return true;
        }
    }

    private IReadOnlyDictionary<string, string> ReadValuesUnlocked()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists(_path))
        {
            return map;
        }

        foreach (var line in ParseLines(File.ReadAllText(_path)))
        {
            if (line.IsKey && line.Key != null)
            {
                map[line.Key] = line.Value ?? string.Empty;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> ValuesOf(List<Line> lines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.IsKey && line.Key != null)
            {
                map[line.Key] = line.Value ?? string.Empty;
            }
        }

        return map;
    }

    private static List<Line> ParseLines(string text)
    {
        var result = new List<Line>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            result.Add(Line.Parse(raw));
        }

        return result;
    }

    /// <summary>一行配置。非键行（注释、空行）整行原样保留。</summary>
    private sealed class Line
    {
        public bool IsKey { get; private init; }
        public string? Key { get; private init; }
        public string? Value { get; private init; }
        public string Text { get; private init; } = string.Empty;

        public static Line Parse(string raw)
        {
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                return new Line { Text = raw };
            }

            var split = trimmed.IndexOf('=');
            var key = trimmed[..split].Trim();
            var value = trimmed[(split + 1)..].Trim();
            if (key.Length == 0)
            {
                return new Line { Text = raw };
            }

            return new Line { IsKey = true, Key = key, Value = value, Text = raw };
        }

        public static Line Setting(string key, string value) =>
            new() { IsKey = true, Key = key, Value = value, Text = $"{key} = {value}" };
    }
}
