using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Microsoft.Data.Sqlite;

namespace Guardian.Storage.Repositories;

/// <summary>系统能力检测报告仓储（保留最新一份 + 历史）。</summary>
public sealed class CapabilityRepository
{
    private readonly GuardianDb _db;

    public CapabilityRepository(GuardianDb db) => _db = db;

    public long Save(CapabilityReport report)
        => _db.QueryScalar<long>("""
            INSERT INTO capability_reports (ts, payload) VALUES ($ts, $payload);
            SELECT last_insert_rowid();
            """,
            new SqliteParameter("$ts", report.GeneratedAt.ToString("o")),
            new SqliteParameter("$payload", ProtocolJson.Serialize(report)));

    public CapabilityReport? GetLatest()
    {
        var json = _db.QueryScalar<string?>("SELECT payload FROM capability_reports ORDER BY id DESC LIMIT 1;");
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return ProtocolJson.Deserialize<CapabilityReport>(json);
        }
        catch
        {
            return null;
        }
    }
}
