using System.Text;
using System.Text.Json;

namespace Guardian.Windows.Streaming;

/// <summary>Sunshine 自己枚举到的一台显示设备。</summary>
public sealed record SunshineDisplayDevice
{
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayPath { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public string ManufacturerId { get; init; } = string.Empty;
    public string ProductCode { get; init; } = string.Empty;
    public bool Primary { get; init; }
}

/// <summary>
/// 从 Sunshine 日志里读出它自己看到的显示设备清单，用于拿到虚拟屏的 output_name（形如 {9acddf6d-…}）。
/// 为什么读日志而不是算：output_name 是 libdisplaydevice 生成的显示标识，本机实测它不落注册表、
/// 也不在 sunshine_state.json 里，只出现在日志的 "Currently available display devices:" JSON 块中
/// （display_device.state 有同一 GUID 但没有厂商字段，无法据此分辨哪台是虚拟屏）；
/// 另按 UUIDv5 用 840 组 namespace×名称×编码尝试复算，全部不匹配（算法已用 Python uuid5 自测通过），
/// 所以这里以 Sunshine 自己的输出为唯一事实来源，不做推算。
/// </summary>
public static class SunshineDisplayDeviceLog
{
    private const string Header = "Currently available display devices:";
    private const long DefaultTailBytes = 2 * 1024 * 1024;

    /// <summary>
    /// 标识"我们自己那块虚拟屏"的线索。刻意不含泛用词 virtual / idd / indirect：
    /// 第三方远程软件也注册虚拟显示适配器（新机实测 ToDesk 的 Todesk Virtual Display Adapter），
    /// 认错目标会让互锁放行到别人的屏幕上，甚至让 Guardian 以为驱动已经装好。
    /// </summary>
    private static readonly string[] VirtualMarkers =
    [
        "vdd",
        "mtt",
        "virtual display driver",
    ];

    /// <summary>取最近一次设备清单。日志由 Sunshine 每次启动截断重写，读尾部即可命中最新块。</summary>
    public static bool TryReadLatest(string logPath, out IReadOnlyList<SunshineDisplayDevice> devices, out string evidence, long tailBytes = DefaultTailBytes)
    {
        devices = Array.Empty<SunshineDisplayDevice>();
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
            // 日志被 Sunshine 独占或正在轮转时，让上层如实报“暂时读不到”，重试即可。
            evidence = $"读取日志失败（Sunshine 正在写入时重试）：{ex.Message}";
            return false;
        }

        var headerAt = text.LastIndexOf(Header, StringComparison.Ordinal);
        if (headerAt < 0)
        {
            evidence = "日志中没有显示设备清单，Sunshine 可能未启动或尚未加载显示设备组件。";
            return false;
        }

        var open = text.IndexOf('[', headerAt);
        var close = open < 0 ? -1 : FindMatchingBracket(text, open);
        if (close < 0)
        {
            evidence = "日志中的显示设备清单不完整（JSON 数组未闭合），本轮不作为依据。";
            return false;
        }

        try
        {
            devices = Parse(text.Substring(open, close - open + 1));
            evidence = $"从日志读到 {devices.Count} 台显示设备。";
            return devices.Count > 0;
        }
        catch (Exception ex)
        {
            evidence = $"显示设备清单解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 在设备清单里挑虚拟屏。命中多条时返回 null：宁可不写配置，也不能把物理面板的标识当成虚拟屏写进
    /// ensure_only_display——那会停用真实输出而把虚拟屏留下，本机就没有任何可用画面。
    /// </summary>
    public static SunshineDisplayDevice? FindVirtual(IReadOnlyList<SunshineDisplayDevice> devices, out string evidence)
    {
        var matches = devices.Where(IsVirtual).ToList();
        if (matches.Count == 1)
        {
            evidence = $"候选：{Describe(matches[0])}";
            return matches[0];
        }

        evidence = matches.Count == 0
            ? $"清单里没有可判定为虚拟屏的设备：{string.Join("；", devices.Select(Describe))}"
            : $"清单里有多台设备像虚拟屏，无法确定唯一：{string.Join("；", matches.Select(Describe))}";
        return null;
    }

    public static bool IsVirtual(SunshineDisplayDevice device)
        => VirtualMarkers.Any(marker =>
            device.FriendlyName.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || device.ManufacturerId.Contains(marker, StringComparison.OrdinalIgnoreCase)
            || device.ProductCode.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static string Describe(SunshineDisplayDevice device)
        => $"{device.DeviceId}「{(string.IsNullOrEmpty(device.FriendlyName) ? device.DisplayPath : device.FriendlyName)}」厂商={device.ManufacturerId}";

    private static List<SunshineDisplayDevice> Parse(string json)
    {
        var result = new List<SunshineDisplayDevice>();
        using var document = JsonDocument.Parse(json);

        foreach (var element in document.RootElement.EnumerateArray())
        {
            result.Add(new SunshineDisplayDevice
            {
                DeviceId = GetString(element, "device_id"),
                DisplayPath = GetString(element, "display_name"),
                FriendlyName = GetString(element, "friendly_name"),
                ManufacturerId = GetString(GetProperty(element, "edid"), "manufacturer_id"),
                ProductCode = GetString(GetProperty(element, "edid"), "product_code"),
                Primary = GetProperty(GetProperty(element, "info"), "primary")?.ValueKind == JsonValueKind.True,
            });
        }

        return result;
    }

    private static JsonElement? GetProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;

    private static JsonElement? GetProperty(JsonElement? element, string name)
        => element is null ? null : GetProperty(element.Value, name);

    private static string GetString(JsonElement? element, string name)
    {
        if (element is null)
        {
            return string.Empty;
        }

        return GetString(element.Value, name);
    }

    private static string GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int FindMatchingBracket(string text, int open)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                case '{':
                    depth++;
                    break;
                case ']':
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
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

        // 尾部可能从一行中间开始，丢掉第一个换行之前的残行，避免截断的 JSON 干扰。
        return Encoding.UTF8.GetString(buffer, 0, read);
    }
}
