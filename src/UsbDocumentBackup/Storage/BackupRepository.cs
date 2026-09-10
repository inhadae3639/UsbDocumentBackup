using System.Globalization;
using Microsoft.Data.Sqlite;

namespace UsbDocumentBackup.Storage;

/// <summary>
/// All reads and writes of persisted state. Local backup state and upload state are stored
/// separately so a healthy local archive is never hidden by a Drive connection problem.
/// </summary>
public sealed class BackupRepository
{
    private readonly BackupDatabase _database;

    public BackupRepository(BackupDatabase database) => _database = database;

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    // ---------- devices ----------

    /// <summary>
    /// Finds the device row for a volume, creating one when the volume has not been seen before.
    /// Matching is by volume identity first; the size/serial/label fingerprint is only a fallback
    /// for volumes that expose no stable identity. A changed drive letter never creates a new row.
    /// </summary>
    public DeviceRecord UpsertDevice(string? volumeId, string fingerprint, string displayName, BusKind busKind, DateTimeOffset seenUtc)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        string? existingId = null;
        if (volumeId is not null)
        {
            existingId = Scalar(connection, transaction, "SELECT id FROM devices WHERE volume_id = $v;", ("$v", volumeId));
        }

        existingId ??= Scalar(
            connection,
            transaction,
            "SELECT id FROM devices WHERE volume_id IS NULL AND fingerprint = $f;",
            ("$f", fingerprint));

        var id = existingId ?? Id.New();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO devices (id, volume_id, fingerprint, display_name, last_seen_utc, bus_kind)
                VALUES ($id, $volume, $fingerprint, $name, $seen, $bus)
                ON CONFLICT(id) DO UPDATE SET
                    volume_id     = excluded.volume_id,
                    fingerprint   = excluded.fingerprint,
                    display_name  = excluded.display_name,
                    last_seen_utc = excluded.last_seen_utc,
                    bus_kind      = excluded.bus_kind;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$volume", (object?)volumeId ?? DBNull.Value);
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$name", displayName);
            command.Parameters.AddWithValue("$seen", Utc(seenUtc));
            command.Parameters.AddWithValue("$bus", busKind.ToString());
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return new DeviceRecord(id, volumeId, fingerprint, displayName, seenUtc, busKind);
    }

    public IReadOnlyList<DeviceRecord> ListDevices()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, volume_id, fingerprint, display_name, last_seen_utc, bus_kind FROM devices ORDER BY last_seen_utc DESC;";
        using var reader = command.ExecuteReader();

        var results = new List<DeviceRecord>();
        while (reader.Read())
        {
            results.Add(new DeviceRecord(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                ReadUtc(reader.GetString(4)),
                Enum.Parse<BusKind>(reader.GetString(5))));
        }

        return results;
    }

    /// <summary>
    /// The Drive folder this device's backups go to, remembered by id. Null until the first upload.
    /// </summary>
    public string? GetDeviceDriveFolder(string deviceId) =>
        Scalar("SELECT drive_folder_id FROM devices WHERE id = $id;", ("$id", deviceId));

    public void SaveDeviceDriveFolder(string deviceId, string driveFolderId) => Execute(
        "UPDATE devices SET drive_folder_id = $folder WHERE id = $id;",
        ("$folder", driveFolderId),
        ("$id", deviceId));

    // ---------- backups ----------

    private const string BackupColumns =
        "id, device_id, relative_path, file_name, size_bytes, source_modified_utc, backed_up_utc, sha256, local_relative_path, state, tier";

    /// <summary>The same columns, in the same order, qualified for queries that join uploads.</summary>
    private const string PrefixedBackupColumns =
        "b.id, b.device_id, b.relative_path, b.file_name, b.size_bytes, b.source_modified_utc, b.backed_up_utc, b.sha256, b.local_relative_path, b.state, b.tier";

    private static BackupRecord ReadBackup(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt64(4),
        ReadUtc(reader.GetString(5)),
        ReadUtc(reader.GetString(6)),
        reader.GetString(7),
        reader.GetString(8),
        Enum.Parse<BackupState>(reader.GetString(9)),
        Enum.Parse<BackupTier>(reader.GetString(10)));

    public void Insert(BackupRecord record)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO backups (" + BackupColumns + ") " +
            "VALUES ($id, $device, $relative, $name, $size, $modified, $backedUp, $hash, $local, $state, $tier);";
        command.Parameters.AddWithValue("$id", record.Id);
        command.Parameters.AddWithValue("$device", record.DeviceId);
        command.Parameters.AddWithValue("$relative", record.RelativePath);
        command.Parameters.AddWithValue("$name", record.FileName);
        command.Parameters.AddWithValue("$size", record.SizeBytes);
        command.Parameters.AddWithValue("$modified", Utc(record.SourceModifiedUtc));
        command.Parameters.AddWithValue("$backedUp", Utc(record.BackedUpUtc));
        command.Parameters.AddWithValue("$hash", record.Sha256);
        command.Parameters.AddWithValue("$local", record.LocalRelativePath);
        command.Parameters.AddWithValue("$state", record.State.ToString());
        command.Parameters.AddWithValue("$tier", record.Tier.ToString());
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Flips a backup to Complete and, for a retained presentation, enqueues the upload in the same
    /// transaction, so a crash can never leave a verified archive file that nobody will ever upload.
    /// Temporary backups are local-only and are deliberately never queued.
    /// </summary>
    public void MarkCompleteAndEnqueue(string backupId, BackupTier tier, DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE backups SET state = $state WHERE id = $id;";
            command.Parameters.AddWithValue("$state", BackupState.Complete.ToString());
            command.Parameters.AddWithValue("$id", backupId);
            command.ExecuteNonQuery();
        }

        if (tier == BackupTier.Retained)
        {
            Enqueue(connection, transaction, backupId, now);
        }

        transaction.Commit();
    }

    private static void Enqueue(SqliteConnection connection, SqliteTransaction transaction, string backupId, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO uploads (backup_id, state, attempts, next_attempt_utc)
            VALUES ($id, $state, 0, $now)
            ON CONFLICT(backup_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", backupId);
        command.Parameters.AddWithValue("$state", UploadState.Waiting.ToString());
        command.Parameters.AddWithValue("$now", Utc(now));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Moves an existing temporary backup up to the retained tier once we learn the document was
    /// opened. The archive file has already been copied and verified, so only its location and
    /// tier change -- the source is never read a second time.
    /// </summary>
    public void PromoteToRetained(string backupId, string newLocalRelativePath, DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "UPDATE backups SET tier = $tier, local_relative_path = $local WHERE id = $id;";
            command.Parameters.AddWithValue("$tier", BackupTier.Retained.ToString());
            command.Parameters.AddWithValue("$local", newLocalRelativePath);
            command.Parameters.AddWithValue("$id", backupId);
            command.ExecuteNonQuery();
        }

        Enqueue(connection, transaction, backupId, now);
        transaction.Commit();
    }

    /// <summary>
    /// Temporary backups past the retention window. Row and file both go.
    /// A document we have recorded as opened is excluded even while it is still labelled
    /// temporary: it is waiting to be promoted, and deleting it would throw away the one copy of a
    /// presentation the user actually opened.
    /// </summary>
    public IReadOnlyList<BackupRecord> ListExpiredTemporary(DateTimeOffset cutoffUtc) => QueryMany(
        "SELECT " + PrefixedBackupColumns + " FROM backups b "
        + "WHERE b.tier = 'Temporary' AND b.state = 'Complete' AND b.backed_up_utc < $cutoff "
        + "AND NOT EXISTS (SELECT 1 FROM opened_documents o "
        + "                WHERE o.device_id = b.device_id AND o.relative_path = b.relative_path) "
        + "ORDER BY b.backed_up_utc;",
        ("$cutoff", Utc(cutoffUtc)));

    /// <summary>
    /// Retained backups that still need their local copy present: the upload has not completed, so
    /// a missing file is a half-finished promotion rather than a copy the sweep released.
    /// </summary>
    public IReadOnlyList<BackupRecord> ListRetainedAwaitingUpload() => QueryMany(
        "SELECT " + PrefixedBackupColumns + " FROM backups b "
        + "LEFT JOIN uploads u ON u.backup_id = b.id "
        + "WHERE b.tier = 'Retained' AND b.state = 'Complete' "
        + "AND (u.state IS NULL OR u.state <> 'Done');");

    /// <summary>
    /// Retained backups whose local copy may be released: old enough, and confirmed present in
    /// Drive. Anything still waiting, uploading or needing attention is excluded, so a local copy
    /// is never dropped while Drive is not known to hold it. The row is kept either way.
    /// </summary>
    public IReadOnlyList<BackupRecord> ListRetainedWithConfirmedUpload(DateTimeOffset cutoffUtc) => QueryMany(
        "SELECT " + PrefixedBackupColumns + " FROM backups b "
        + "JOIN uploads u ON u.backup_id = b.id "
        + "WHERE b.tier = 'Retained' AND b.state = 'Complete' AND u.state = 'Done' "
        + "AND u.drive_file_id IS NOT NULL AND b.backed_up_utc < $cutoff "
        + "ORDER BY b.backed_up_utc;",
        ("$cutoff", Utc(cutoffUtc)));

    public void DeleteBackup(string backupId)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM uploads WHERE backup_id = $id;", ("$id", backupId));
        Execute(connection, transaction, "DELETE FROM backups WHERE id = $id;", ("$id", backupId));
        transaction.Commit();
    }

    public BackupRecord? GetBackup(string backupId) =>
        QueryOne("SELECT " + BackupColumns + " FROM backups WHERE id = $id;", ("$id", backupId));

    /// <summary>Most recent verified backup of one source path, used for change detection.</summary>
    public BackupRecord? GetLatestComplete(string deviceId, string relativePath) => QueryOne(
        "SELECT " + BackupColumns + " FROM backups " +
        "WHERE device_id = $device AND relative_path = $relative AND state = 'Complete' " +
        "ORDER BY backed_up_utc DESC LIMIT 1;",
        ("$device", deviceId),
        ("$relative", relativePath));

    public IReadOnlyList<BackupRecord> ListPending() =>
        QueryMany("SELECT " + BackupColumns + " FROM backups WHERE state = 'Pending';");

    public IReadOnlyList<BackupRecord> ListVersions(string deviceId, string relativePath) => QueryMany(
        "SELECT " + BackupColumns + " FROM backups " +
        "WHERE device_id = $device AND relative_path = $relative AND state = 'Complete' " +
        "ORDER BY backed_up_utc DESC;",
        ("$device", deviceId),
        ("$relative", relativePath));

    /// <summary>Free-text search over file name and original path for the restore window.</summary>
    public IReadOnlyList<BackupRecord> Search(string? term, int limit = 500)
    {
        var pattern = string.IsNullOrWhiteSpace(term) ? "%" : "%" + term.Trim() + "%";
        return QueryMany(
            "SELECT " + BackupColumns + " FROM backups " +
            "WHERE state = 'Complete' AND (file_name LIKE $term OR relative_path LIKE $term) " +
            "ORDER BY backed_up_utc DESC LIMIT $limit;",
            ("$term", pattern),
            ("$limit", limit));
    }

    public int CountBackups(BackupState state) => int.Parse(
        Scalar("SELECT COUNT(*) FROM backups WHERE state = $state;", ("$state", state.ToString())) ?? "0",
        CultureInfo.InvariantCulture);

    public int CountBackups(BackupState state, BackupTier tier) => int.Parse(
        Scalar(
            "SELECT COUNT(*) FROM backups WHERE state = $state AND tier = $tier;",
            ("$state", state.ToString()),
            ("$tier", tier.ToString())) ?? "0",
        CultureInfo.InvariantCulture);

    // ---------- uploads ----------

    public int CountUploads(UploadState state) => int.Parse(
        Scalar("SELECT COUNT(*) FROM uploads WHERE state = $state;", ("$state", state.ToString())) ?? "0",
        CultureInfo.InvariantCulture);

    /// <summary>
    /// Uploads left mid-flight by a crash cannot be trusted; Stage C re-checks the remote side
    /// before retrying, so they are returned to the waiting queue on startup.
    /// </summary>
    public int ResetInterruptedUploads(DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE uploads SET state = $waiting, next_attempt_utc = $now WHERE state = $uploading;";
        command.Parameters.AddWithValue("$waiting", UploadState.Waiting.ToString());
        command.Parameters.AddWithValue("$uploading", UploadState.Uploading.ToString());
        command.Parameters.AddWithValue("$now", Utc(now));
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Takes the next upload that is due and marks it in flight, so only one worker touches it.
    /// Returns the backup alongside it because the worker needs the file, its size and its hash.
    /// </summary>
    public (BackupRecord Backup, UploadRecord Upload)? ClaimNextUpload(DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        BackupRecord? backup = null;
        UploadRecord? upload = null;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "SELECT " + PrefixedBackupColumns
                + ", u.state, u.drive_folder_id, u.drive_file_id, u.attempts, u.next_attempt_utc, u.last_error, u.account_key, u.resume_uri, u.uploaded_bytes "
                + "FROM uploads u JOIN backups b ON b.id = u.backup_id "
                + "WHERE u.state = 'Waiting' AND (u.next_attempt_utc IS NULL OR u.next_attempt_utc <= $now) "
                + "ORDER BY u.next_attempt_utc LIMIT 1;";
            command.Parameters.AddWithValue("$now", Utc(now));

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            backup = ReadBackup(reader);
            upload = new UploadRecord(
                backup.Id,
                Enum.Parse<UploadState>(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetString(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.GetInt32(14),
                reader.IsDBNull(15) ? null : ReadUtc(reader.GetString(15)),
                reader.IsDBNull(16) ? null : reader.GetString(16),
                reader.IsDBNull(17) ? null : reader.GetString(17),
                reader.IsDBNull(18) ? null : reader.GetString(18),
                reader.GetInt64(19));
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE uploads SET state = 'Uploading' WHERE backup_id = $id;";
            command.Parameters.AddWithValue("$id", backup.Id);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return (backup, upload with { State = UploadState.Uploading });
    }

    /// <summary>
    /// Records the reserved Drive id and target folder before a single byte is sent. Reusing this
    /// id on every retry is what prevents a lost success response from creating a duplicate.
    /// </summary>
    public void SaveUploadTarget(string backupId, string driveFolderId, string driveFileId, string accountKey)
    {
        Execute(
            "UPDATE uploads SET drive_folder_id = $folder, drive_file_id = $file, account_key = $account WHERE backup_id = $id;",
            ("$folder", driveFolderId),
            ("$file", driveFileId),
            ("$account", accountKey),
            ("$id", backupId));
    }

    public void SaveUploadProgress(string backupId, string? resumeUri, long uploadedBytes) => Execute(
        "UPDATE uploads SET resume_uri = $uri, uploaded_bytes = $bytes WHERE backup_id = $id;",
        ("$uri", (object?)resumeUri ?? DBNull.Value),
        ("$bytes", uploadedBytes),
        ("$id", backupId));

    public void MarkUploadDone(string backupId, string driveFileId) => Execute(
        "UPDATE uploads SET state = 'Done', drive_file_id = $file, last_error = NULL, resume_uri = NULL WHERE backup_id = $id;",
        ("$file", driveFileId),
        ("$id", backupId));

    /// <summary>A transient failure: try again after the given moment, keeping any progress.</summary>
    public void MarkUploadRetry(string backupId, string error, DateTimeOffset nextAttemptUtc) => Execute(
        "UPDATE uploads SET state = 'Waiting', attempts = attempts + 1, next_attempt_utc = $next, last_error = $error WHERE backup_id = $id;",
        ("$next", Utc(nextAttemptUtc)),
        ("$error", error),
        ("$id", backupId));

    /// <summary>
    /// A failure no amount of waiting will fix: out of Drive space, permission denied, revoked
    /// authorisation. Deliberately not retried on a timer.
    /// </summary>
    public void MarkUploadNeedsAttention(string backupId, string error) => Execute(
        "UPDATE uploads SET state = 'NeedsAttention', attempts = attempts + 1, next_attempt_utc = NULL, last_error = $error WHERE backup_id = $id;",
        ("$error", error),
        ("$id", backupId));

    /// <summary>Puts everything that needs attention back in the queue, after a reconnect.</summary>
    public int RequeueAllNeedingAttention(DateTimeOffset now) => Execute(
        "UPDATE uploads SET state = 'Waiting', next_attempt_utc = $now, attempts = 0 WHERE state = 'NeedsAttention';",
        ("$now", Utc(now)));

    public UploadRecord? GetUpload(string backupId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT backup_id, state, drive_folder_id, drive_file_id, attempts, next_attempt_utc, last_error, account_key, resume_uri, uploaded_bytes "
            + "FROM uploads WHERE backup_id = $id;";
        command.Parameters.AddWithValue("$id", backupId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new UploadRecord(
            reader.GetString(0),
            Enum.Parse<UploadState>(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt32(4),
            reader.IsDBNull(5) ? null : ReadUtc(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetInt64(9));
    }

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    // ---------- opened documents ----------

    /// <summary>
    /// Records that a document was seen open. This is the single fact that decides whether a
    /// presentation is worth keeping and uploading, so it is written the moment the owner file
    /// appears rather than waiting for the copy to finish.
    /// </summary>
    public void MarkOpened(string deviceId, string relativePath, DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO opened_documents (device_id, relative_path, first_seen_utc, last_seen_utc)
            VALUES ($device, $relative, $now, $now)
            ON CONFLICT(device_id, relative_path) DO UPDATE SET last_seen_utc = excluded.last_seen_utc;
            """;
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$relative", relativePath);
        command.Parameters.AddWithValue("$now", Utc(now));
        command.ExecuteNonQuery();
    }

    public HashSet<string> ListOpenedPaths(string deviceId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT relative_path FROM opened_documents WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId);
        using var reader = command.ExecuteReader();

        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }

    // ---------- issues ----------

    public void LogIssue(string kind, string? deviceId, string? path, string? detail, DateTimeOffset now)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO scan_issues (occurred_utc, device_id, path, kind, detail) VALUES ($now, $device, $path, $kind, $detail);";
        command.Parameters.AddWithValue("$now", Utc(now));
        command.Parameters.AddWithValue("$device", (object?)deviceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ScanIssue> RecentIssues(int limit = 100)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, occurred_utc, device_id, path, kind, detail FROM scan_issues ORDER BY id DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();

        var results = new List<ScanIssue>();
        while (reader.Read())
        {
            results.Add(new ScanIssue(
                reader.GetInt64(0),
                ReadUtc(reader.GetString(1)),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return results;
    }

    // ---------- helpers ----------

    private BackupRecord? QueryOne(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBackup(reader) : null;
    }

    private IReadOnlyList<BackupRecord> QueryMany(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var results = new List<BackupRecord>();
        while (reader.Read())
        {
            results.Add(ReadBackup(reader));
        }

        return results;
    }

    private string? Scalar(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = _database.Open();
        return Scalar(connection, null, sql, parameters);
    }

    private static string? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        // ExecuteScalar returns DBNull for a SQL NULL, and DBNull.ToString() is "", which would
        // turn "no folder recorded" into "a folder named empty string".
        var scalar = command.ExecuteScalar();
        return scalar is null or DBNull ? null : scalar.ToString();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
