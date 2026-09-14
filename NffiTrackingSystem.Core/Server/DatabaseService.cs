using Microsoft.Data.Sqlite;
using NffiTrackingSystem.Shared.Models;

namespace NffiTrackingSystem.Core.Server;

// Immutable record representing one row from the NffiPositions table.
// Used when reading history back to the UI for replay.
public sealed record PositionRecord(
    int Id,
    string UnitId,
    string UnitName,
    double Latitude,
    double Longitude,
    double Altitude,
    double Velocity,
    double Heading,
    string OperationalStatus,
    DateTime ReportedAtUtc,
    DateTime ReceivedAtUtc,
    int SequenceNumber);

// SQLite storage for NFFI positions and last-seen unit state.
public sealed class DatabaseService : IDisposable
{
    // SQLite connection string (e.g. "Data Source=nffi_tracking.db")
    private readonly string _cs;

    public DatabaseService(string dbPath)
    {
        _cs = $"Data Source={dbPath}";
    }

    // Creates the schema if it does not exist yet.
    // Called once at server startup.
    public async Task InitializeAsync()
    {
        await using var conn = new SqliteConnection(_cs);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS NffiPositions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UnitId TEXT NOT NULL,
                UnitName TEXT,
                Latitude REAL NOT NULL,
                Longitude REAL NOT NULL,
                Altitude REAL,
                Velocity REAL,
                Heading REAL,
                OperationalStatus TEXT,
                ReportedAtUtc TEXT NOT NULL,
                ReceivedAtUtc TEXT NOT NULL,
                JreapSequenceNumber INTEGER,
                RawXml TEXT);

            CREATE INDEX IF NOT EXISTS IX_NffiPositions_UnitId_ReportedAt
                ON NffiPositions (UnitId, ReportedAtUtc DESC);

            CREATE TABLE IF NOT EXISTS ConnectedUnits (
                UnitId TEXT PRIMARY KEY,
                UnitName TEXT,
                LastSeenUtc TEXT NOT NULL,
                LastLatitude REAL,
                LastLongitude REAL);";

        await cmd.ExecuteNonQueryAsync();
    }

    // Inserts a single NFFI position and updates the "last seen" row for the unit.
    // Both operations run inside one transaction.
    public async Task InsertPositionAsync(NffiMessage msg, int seq, string rawXml)
    {
        await using var conn = new SqliteConnection(_cs);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();

        // Insert into the full history table
        var i = conn.CreateCommand();
        i.Transaction = (SqliteTransaction)tx;
        i.CommandText = @"
            INSERT INTO NffiPositions
                (UnitId, UnitName, Latitude, Longitude, Altitude, Velocity, Heading,
                 OperationalStatus, ReportedAtUtc, ReceivedAtUtc, JreapSequenceNumber, RawXml)
            VALUES
                ($u, $n, $la, $lo, $al, $v, $h, $s, $r, $rc, $q, $x);";

        i.Parameters.AddWithValue("$u", msg.Identification.UnitId);
        i.Parameters.AddWithValue("$n", msg.Identification.Name ?? "");
        i.Parameters.AddWithValue("$la", msg.PositionalData.Coordinates.Latitude);
        i.Parameters.AddWithValue("$lo", msg.PositionalData.Coordinates.Longitude);
        i.Parameters.AddWithValue("$al", msg.PositionalData.Coordinates.Altitude);
        i.Parameters.AddWithValue("$v", msg.PositionalData.Velocity);
        i.Parameters.AddWithValue("$h", msg.PositionalData.Heading);
        i.Parameters.AddWithValue("$s", msg.Status.OperationalStatus ?? "");
        i.Parameters.AddWithValue("$r", msg.Status.TimestampUtc.ToString("O"));
        i.Parameters.AddWithValue("$rc", DateTime.UtcNow.ToString("O"));
        i.Parameters.AddWithValue("$q", seq);
        i.Parameters.AddWithValue("$x", rawXml);
        await i.ExecuteNonQueryAsync();

        // Upsert into the "last known state" table (one row per unit)
        var u = conn.CreateCommand();
        u.Transaction = (SqliteTransaction)tx;
        u.CommandText = @"
            INSERT INTO ConnectedUnits (UnitId, UnitName, LastSeenUtc, LastLatitude, LastLongitude)
            VALUES ($u, $n, $now, $la, $lo)
            ON CONFLICT(UnitId) DO UPDATE SET
                UnitName = excluded.UnitName,
                LastSeenUtc = excluded.LastSeenUtc,
                LastLatitude = excluded.LastLatitude,
                LastLongitude = excluded.LastLongitude;";

        u.Parameters.AddWithValue("$u", msg.Identification.UnitId);
        u.Parameters.AddWithValue("$n", msg.Identification.Name ?? "");
        u.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        u.Parameters.AddWithValue("$la", msg.PositionalData.Coordinates.Latitude);
        u.Parameters.AddWithValue("$lo", msg.PositionalData.Coordinates.Longitude);
        await u.ExecuteNonQueryAsync();

        await tx.CommitAsync();
    }

    // Returns the distinct unit IDs present in the history table.
    public async Task<List<string>> GetUnitIdsAsync()
    {
        var list = new List<string>();
        await using var conn = new SqliteConnection(_cs);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT UnitId FROM NffiPositions ORDER BY UnitId;";

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    // Returns the earliest and latest ReportedAtUtc timestamps in the history table.
    // Returns null when the table is empty.
    public async Task<(DateTime Min, DateTime Max)?> GetHistoryRangeAsync()
    {
        await using var conn = new SqliteConnection(_cs);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MIN(ReportedAtUtc), MAX(ReportedAtUtc) FROM NffiPositions;";

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync() || r.IsDBNull(0)) return null;

        return (DateTime.Parse(r.GetString(0)), DateTime.Parse(r.GetString(1)));
    }

    // Returns the full history of positions, optionally filtered by unit ID.
    // Rows are ordered by ReportedAtUtc ascending so the replay runs forward in time.
    public async Task<List<PositionRecord>> GetHistoryAsync(string? unitId = null)
    {
        var list = new List<PositionRecord>();
        await using var conn = new SqliteConnection(_cs);
        await conn.OpenAsync();

        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT Id, UnitId, UnitName, Latitude, Longitude, Altitude,
                   Velocity, Heading, OperationalStatus, ReportedAtUtc,
                   ReceivedAtUtc, JreapSequenceNumber
            FROM NffiPositions
            WHERE (@u IS NULL OR UnitId = @u)
            ORDER BY ReportedAtUtc ASC;";

        cmd.Parameters.AddWithValue("@u", (object?)unitId ?? DBNull.Value);

        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            // Handle nullable columns gracefully
            list.Add(new PositionRecord(
                r.GetInt32(0),
                r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2),
                r.GetDouble(3),
                r.GetDouble(4),
                r.IsDBNull(5) ? 0 : r.GetDouble(5),
                r.IsDBNull(6) ? 0 : r.GetDouble(6),
                r.IsDBNull(7) ? 0 : r.GetDouble(7),
                r.IsDBNull(8) ? "" : r.GetString(8),
                DateTime.Parse(r.GetString(9)),
                DateTime.Parse(r.GetString(10)),
                r.IsDBNull(11) ? 0 : r.GetInt32(11)));
        }
        return list;
    }

    // No unmanaged resources to release; kept for symmetry with IDisposable pattern.
    public void Dispose() { }
}