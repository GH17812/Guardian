using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Microsoft.Data.Sqlite;

namespace Guardian.Storage.Repositories;

/// <summary>设置仓储：key-value + 强类型 GuardianSettings（JSON 序列化）。</summary>
public sealed class SettingsRepository
{
    private const string GuardianSettingsKey = "guardian-settings";

    private readonly GuardianDb _db;

    public SettingsRepository(GuardianDb db) => _db = db;

    public string? GetString(string key)
        => _db.QueryScalar<string>("SELECT value FROM settings WHERE key = $k;", new SqliteParameter("$k", key));

    public void SetString(string key, string value)
        => _db.Execute(
            "INSERT INTO settings (key, value, updated_at) VALUES ($k, $v, $t) ON CONFLICT(key) DO UPDATE SET value = $v, updated_at = $t;",
            new SqliteParameter("$k", key), new SqliteParameter("$v", value), new SqliteParameter("$t", DateTimeOffset.Now.ToString("o")));

    public GuardianSettings LoadGuardianSettings(GuardianSettings fallback)
    {
        var json = GetString(GuardianSettingsKey);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }
        try
        {
            return ProtocolJson.Deserialize<GuardianSettings>(json) ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    public void SaveGuardianSettings(GuardianSettings settings)
        => SetString(GuardianSettingsKey, ProtocolJson.Serialize(settings));
}
