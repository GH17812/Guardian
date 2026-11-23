using Guardian.Contracts;
using Guardian.Contracts.Models;
using Guardian.Contracts.Protocol;
using Microsoft.Data.Sqlite;

namespace Guardian.Storage.Repositories;

/// <summary>恢复记录仓储。</summary>
public sealed class RecoveryRepository
{
    private readonly GuardianDb _db;

    public RecoveryRepository(GuardianDb db) => _db = db;

    public long Add(RecoveryRecord record)
        => _db.QueryScalar<long>("""
            INSERT INTO recovery_records (ts, reason, source, success, steps, detail)
            VALUES ($ts, $reason, $source, $success, $steps, $detail);
            SELECT last_insert_rowid();
            """,
            new SqliteParameter("$ts", record.Timestamp.ToString("o")),
            new SqliteParameter("$reason", record.Reason.ToString()),
            new SqliteParameter("$source", record.Source.ToString()),
            new SqliteParameter("$success", record.Success ? 1 : 0),
            new SqliteParameter("$steps", ProtocolJson.Serialize(record.Steps)),
            new SqliteParameter("$detail", record.Detail));

    public List<RecoveryRecord> GetRecent(int maxCount)
    {
        var limit = Math.Clamp(maxCount, 1, 1000);
        return _db.Query($"""
            SELECT * FROM (SELECT * FROM recovery_records ORDER BY id DESC LIMIT $limit) ORDER BY id;
            """, Map, new SqliteParameter("$limit", limit));
    }

    private static RecoveryRecord Map(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Timestamp = DateTimeOffset.Parse(r.GetString(r.GetOrdinal("ts"))),
        Reason = Enum.Parse<RecoveryReason>(r.GetString(r.GetOrdinal("reason"))),
        Source = Enum.Parse<RecoverySource>(r.GetString(r.GetOrdinal("source"))),
        Success = r.GetInt64(r.GetOrdinal("success")) != 0,
        Steps = ProtocolJson.Deserialize<List<RecoveryStepRecord>>(r.GetString(r.GetOrdinal("steps"))) ?? new(),
        Detail = r.IsDBNull(r.GetOrdinal("detail")) ? null : r.GetString(r.GetOrdinal("detail")),
    };
}
