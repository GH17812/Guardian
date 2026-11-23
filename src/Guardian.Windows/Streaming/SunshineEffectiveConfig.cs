using System.Text;
using System.Text.RegularExpressions;

namespace Guardian.Windows.Streaming;

/// <summary>
/// 从 Sunshine 日志读出"当前进程实际生效的配置"。
/// 依据是实测到的日志行形态 <c>config: 'output_name' = {9acddf6d-…}</c>：Sunshine 每次启动、每次改配置都会重打这些行，
/// 因此取每个键的最后一次出现即代表运行中进程持有的值。
/// 这个事实的价值在于能与磁盘上的配置文件对照——两者不一致就是"改了没生效"的危险中间态，界面必须如实显示。
/// </summary>
public static class SunshineEffectiveConfig
{
    private const long DefaultTailBytes = 2 * 1024 * 1024;

    private static readonly Regex Line = new(
        @"config:\s*'(?<key>[^']+)'(?:\s*=\s*(?<value>.*?))?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>启动标记：每轮启动都以这行开头，生效值只能读它之后的块。</summary>
    private static readonly Regex StartupMarker = new(
        @"\]: Info: Sunshine version:",
        RegexOptions.Compiled);

    public static bool TryRead(string logPath, out IReadOnlyDictionary<string, string> values, out string evidence, long tailBytes = DefaultTailBytes)
    {
        var collected = new Dictionary<string, string>(StringComparer.Ordinal);
        values = collected;
        evidence = string.Empty;

        if (!File.Exists(logPath))
        {
            evidence = $"未找到 Sunshine 日志：{logPath}";
            return false;
        }

        string text;
        try
        {
            text = ReadTail(logPath, tailBytes);
        }
        catch (Exception ex)
        {
            evidence = $"读取日志失败（Sunshine 正在写入时重试）：{ex.Message}";
            return false;
        }

        // 只解析最后一个启动块之后的 config: 行。此前取"整段尾部最后一次出现"会把上一次启动的旧值
        // 当成当前生效值：恢复/接管重启后新块还没写完时，读到的还是旧形态，实测表现为恢复后
        // 四步全 Ok 却判 PartialFailure（file 已还原、effective 仍是旧值）。
        var blockStart = 0;
        foreach (Match marker in StartupMarker.Matches(text))
        {
            blockStart = marker.Index;
        }

        if (blockStart == 0 && !StartupMarker.IsMatch(text))
        {
            evidence = "日志尾部没有启动块，无法判定运行中的生效值。";
            return false;
        }

        foreach (Match match in Line.Matches(text, blockStart))
        {
            // 块内后出现的覆盖先出现的：最后一次打印才是当前进程持有的值。
            collected[match.Groups["key"].Value] = match.Groups["value"].Value.Trim();
        }

        if (collected.Count == 0)
        {
            evidence = "最新启动块里没有 config: 行，按不接管处理。";
            return false;
        }

        evidence = $"从最新启动块读到 {collected.Count} 个生效配置键。";
        return true;
    }

    private static string ReadTail(string path, long tailBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var take = Math.Min(stream.Length, tailBytes);
        stream.Seek(stream.Length - take, SeekOrigin.Begin);

        var buffer = new byte[take];
        var read = 0;
        while (read < take)
        {
            var got = stream.Read(buffer, read, (int)(take - read));
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
