namespace Guardian.Windows.Input;

/// <summary>紧急恢复快捷键主键名 → 虚拟键码映射。</summary>
public static class HotkeyMap
{
    private static readonly IReadOnlyDictionary<string, int> Map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["F8"] = 0x77,
        ["F9"] = 0x78,
        ["F10"] = 0x79,
        ["F11"] = 0x7A,
        ["F12"] = 0x7B,
        ["Pause"] = 0x13,
    };

    public static IReadOnlyList<string> SupportedKeys { get; } = Map.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public static int Resolve(string keyName)
        => Map.TryGetValue(keyName, out var vk) ? vk : 0x7B; // 默认 F12

    public static string Normalize(string keyName)
        => Map.ContainsKey(keyName) ? keyName : "F12";
}
