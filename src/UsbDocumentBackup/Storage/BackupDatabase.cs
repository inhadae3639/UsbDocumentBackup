using Microsoft.Data.Sqlite;

namespace UsbDocumentBackup.Storage;

/// <summary>
/// SQLite state store. Devices, backups, the upload queue and scan issues all live here so a
/// restart can reconcile them against the archive directory in one pass.
/// </summary>
public sealed class BackupDatabase
{
    private readonly string _connectionString;

    public BackupDatabase(string databaseFile)
    {
        DatabaseFile = databaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databaseFile,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DatabaseFile { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    public void Migrate()
    {
        var directory = Path.GetDirectoryName(DatabaseFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS devices (
                id            TEXT PRIMARY KEY,
                volume_id     TEXT,
                fingerprint   TEXT NOT NULL,
                display_name  TEXT NOT NULL,
                last_seen_utc TEXT NOT NULL,
                bus_kind      TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS ix_devices_volume
                ON devices(volume_id) WHERE volume_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_devices_fingerprint ON devices(fingerprint);

            CREATE TABLE IF NOT EXISTS backups (
                id                  TEXT PRIMARY KEY,
                device_id           TEXT NOT NULL REFERENCES devices(id),
                relative_path       TEXT NOT NULL,
                file_name           TEXT NOT NULL,
                size_bytes          INTEGER NOT NULL,
                source_modified_utc TEXT NOT NULL,
                backed_up_utc       TEXT NOT NULL,
                sha256              TEXT NOT NULL,
                local_relative_path TEXT NOT NULL,
                state               TEXT NOT NULL,
                tier                TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS ix_backups_lookup
                ON backups(device_id, relative_path, backed_up_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_backups_state ON backups(state);
            CREATE INDEX IF NOT EXISTS ix_backups_name ON backups(file_name);
            CREATE INDEX IF NOT EXISTS ix_backups_tier ON backups(tier, backed_up_utc);

            CREATE TABLE IF NOT EXISTS uploads (
                backup_id        TEXT PRIMARY KEY REFERENCES backups(id),
                state            TEXT NOT NULL,
                drive_folder_id  TEXT,
                drive_file_id    TEXT,
                attempts         INTEGER NOT NULL DEFAULT 0,
                next_attempt_utc TEXT,
                last_error       TEXT,
                account_key      TEXT,
                -- Google's resumable upload session. Persisted so an upload interrupted by a
                -- shutdown continues from where it stopped instead of starting over.
                resume_uri       TEXT,
                uploaded_bytes   INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_uploads_state ON uploads(state, next_attempt_utc);

            -- An Office owner file only exists while the document is open, so the fact that we
            -- once saw one has to outlive both the document being closed and the app restarting.
            CREATE TABLE IF NOT EXISTS opened_documents (
                device_id      TEXT NOT NULL REFERENCES devices(id),
                relative_path  TEXT NOT NULL,
                first_seen_utc TEXT NOT NULL,
                last_seen_utc  TEXT NOT NULL,
                PRIMARY KEY (device_id, relative_path)
            );

            CREATE TABLE IF NOT EXISTS scan_issues (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_utc TEXT NOT NULL,
                device_id    TEXT,
                path         TEXT,
                kind         TEXT NOT NULL,
                detail       TEXT
            );
            """;
        command.ExecuteNonQuery();
    }
}
