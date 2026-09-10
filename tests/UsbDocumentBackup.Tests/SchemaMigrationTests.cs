using Microsoft.Data.Sqlite;
using UsbDocumentBackup.Storage;
using Xunit;

namespace UsbDocumentBackup.Tests;

/// <summary>
/// Every other test starts from an empty database, which is exactly why a real upgrade broke:
/// the schema is created with "IF NOT EXISTS", so on an existing installation the statements did
/// nothing and a column added later was simply absent. Uploads then failed with
/// "no such column: drive_folder_id" on every attempt.
///
/// These start from a database shaped like an older release.
/// </summary>
public sealed class SchemaMigrationTests
{
    /// <summary>The devices table as it existed before the Drive folder id was added.</summary>
    private const string OldDevicesTable =
        """
        CREATE TABLE devices (
            id            TEXT PRIMARY KEY,
            volume_id     TEXT,
            fingerprint   TEXT NOT NULL,
            display_name  TEXT NOT NULL,
            last_seen_utc TEXT NOT NULL,
            bus_kind      TEXT NOT NULL
        );
        """;

    private static string NewDatabaseFile(TestWorkspace workspace) =>
        Path.Combine(workspace.Root, "old-schema", "state.db");

    private static void CreateOldDatabase(string file, string schema)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = file,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Upgrading_an_existing_database_adds_a_column_introduced_later()
    {
        using var workspace = new TestWorkspace();
        var file = NewDatabaseFile(workspace);
        CreateOldDatabase(file, OldDevicesTable);

        var database = new BackupDatabase(file);
        database.Migrate();

        using var connection = database.Open();
        Assert.True(BackupDatabase.HasColumn(connection, "devices", "drive_folder_id"));
    }

    [Fact]
    public void Existing_rows_survive_the_upgrade()
    {
        using var workspace = new TestWorkspace();
        var file = NewDatabaseFile(workspace);
        CreateOldDatabase(
            file,
            OldDevicesTable
            + "INSERT INTO devices VALUES ('dev1', '\\\\?\\Volume{x}\\', 'fp', '내 USB', '2026-09-01T00:00:00+00:00', 'Usb');");

        var database = new BackupDatabase(file);
        database.Migrate();

        var repository = new BackupRepository(database);
        var device = Assert.Single(repository.ListDevices());
        Assert.Equal("dev1", device.Id);
        Assert.Equal("내 USB", device.DisplayName);

        // The new column reads as empty rather than throwing, and can then be written.
        Assert.Null(repository.GetDeviceDriveFolder("dev1"));
        repository.SaveDeviceDriveFolder("dev1", "folder-1");
        Assert.Equal("folder-1", repository.GetDeviceDriveFolder("dev1"));
    }

    [Fact]
    public void Migrating_twice_is_harmless()
    {
        using var workspace = new TestWorkspace();
        var file = NewDatabaseFile(workspace);
        CreateOldDatabase(file, OldDevicesTable);

        var database = new BackupDatabase(file);
        database.Migrate();
        database.Migrate();

        using var connection = database.Open();
        Assert.True(BackupDatabase.HasColumn(connection, "devices", "drive_folder_id"));
    }

    [Fact]
    public void A_backups_table_without_a_tier_column_is_upgraded_with_a_usable_default()
    {
        using var workspace = new TestWorkspace();
        var file = NewDatabaseFile(workspace);
        CreateOldDatabase(
            file,
            OldDevicesTable
            + """
              CREATE TABLE backups (
                  id                  TEXT PRIMARY KEY,
                  device_id           TEXT NOT NULL,
                  relative_path       TEXT NOT NULL,
                  file_name           TEXT NOT NULL,
                  size_bytes          INTEGER NOT NULL,
                  source_modified_utc TEXT NOT NULL,
                  backed_up_utc       TEXT NOT NULL,
                  sha256              TEXT NOT NULL,
                  local_relative_path TEXT NOT NULL,
                  state               TEXT NOT NULL
              );
              INSERT INTO devices VALUES ('dev1','v','fp','내 USB','2026-09-01T00:00:00+00:00','Usb');
              INSERT INTO backups VALUES ('b1','dev1','발표.pptx','발표.pptx',10,
                  '2026-09-01T00:00:00+00:00','2026-09-01T00:00:00+00:00','abc','temp/dev1/b1/발표.pptx','Complete');
              """);

        var database = new BackupDatabase(file);
        database.Migrate();

        var repository = new BackupRepository(database);
        var backup = Assert.Single(repository.Search(null));

        // A row from before tiers existed must read back as something, not blow up on Enum.Parse.
        Assert.Equal(BackupTier.Temporary, backup.Tier);
        Assert.Equal("발표.pptx", backup.FileName);
    }

    [Fact]
    public void A_fresh_database_still_has_every_column()
    {
        using var workspace = new TestWorkspace();
        var file = NewDatabaseFile(workspace);

        var database = new BackupDatabase(file);
        database.Migrate();

        using var connection = database.Open();
        Assert.True(BackupDatabase.HasColumn(connection, "devices", "drive_folder_id"));
        Assert.True(BackupDatabase.HasColumn(connection, "backups", "tier"));
        Assert.True(BackupDatabase.HasColumn(connection, "uploads", "resume_uri"));
        Assert.True(BackupDatabase.HasColumn(connection, "uploads", "uploaded_bytes"));
        Assert.True(BackupDatabase.HasColumn(connection, "uploads", "account_key"));
    }
}
