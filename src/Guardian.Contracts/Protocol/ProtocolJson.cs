using System.Text.Json;
using System.Text.Json.Serialization;

namespace Guardian.Contracts.Protocol;

/// <summary>IPC 消息序列化选项（单一实例，避免重复创建）。</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static JsonElement ToElement<T>(T value)
    {
        var json = Serialize(value);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    public static T? FromElement<T>(JsonElement? element)
        => element is { } e ? e.Deserialize<T>(Options) : default;
}
