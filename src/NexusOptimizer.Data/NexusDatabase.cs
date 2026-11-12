using Microsoft.Data.Sqlite;

namespace NexusOptimizer.Data;

/// <summary>
/// Lokale SQLite-Datenbank der Anwendung. Legt das Schema an, führt Migrationen über PRAGMA user_version aus und
/// stellt bei einer beschädigten Datei eine neue Datenbank bereit (die beschädigte Datei bleibt zur Analyse erhalten).
/// </summary>
public sealed class NexusDatabase
{
    public const int SchemaVersion = 1;
    private readonly string _connectionString;

    public string FilePath { get; }
    /// <summary>Pfad der beiseitegelegten Datei, falls die Datenbank beim Start beschädigt war.</summary>
    public string? CorruptBackupPath { get; private set; }

    public NexusDatabase(string filePath)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = FilePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, DefaultTimeout = 15 }.ToString();

        try
        {
            Initialize();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26) // SQLITE_CORRUPT, SQLITE_NOTADB
        {
            SqliteConnection.ClearAllPools();
            CorruptBackupPath = FilePath + ".beschaedigt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(FilePath, CorruptBackupPath, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
            {
                try { File.Delete(FilePath + suffix); } catch (IOException) { }
            }
            Initialize();
        }
    }

    public static string DefaultPath => Path.Combine(DataDirectory, "nexus.db");

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusOptimizerAI");

    /// <summary>Öffnet eine Verbindung. <paramref name="durable"/> = jeder Commit wird sofort auf den Datenträger geschrieben (Journal).</summary>
    public SqliteConnection Open(bool durable = true)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA synchronous={(durable ? "FULL" : "NORMAL")}; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteScalar();
        }

        // Vollständigkeitsprüfung erkennt beschädigte Dateien, die sich noch öffnen lassen.
        using (var check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check(1);";
            if (check.ExecuteScalar() as string != "ok") throw new SqliteException("Datenbank beschädigt.", 11);
        }

        long version;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = (long)read.ExecuteScalar()!;
        }
        if (version > SchemaVersion)
            throw new InvalidOperationException($"Die Datenbank stammt von einer neueren Programmversion (Schema {version}). Bitte aktualisieren Sie die Anwendung.");

        if (version < 1)
        {
            using var transaction = connection.BeginTransaction();
            using var create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS transactions (
                    id TEXT PRIMARY KEY,
                    seq INTEGER NOT NULL,
                    batch_id TEXT NOT NULL,
                    timestamp INTEGER NOT NULL,
                    action_id TEXT NOT NULL,
                    action_name TEXT NOT NULL,
                    parameters_json TEXT NOT NULL,
                    source INTEGER NOT NULL,
                    source_label TEXT NOT NULL,
                    status INTEGER NOT NULL,
                    risk INTEGER NOT NULL,
                    privilege INTEGER NOT NULL,
                    restart INTEGER NOT NULL,
                    reversible INTEGER NOT NULL,
                    backup_json TEXT NULL,
                    original_display TEXT NOT NULL,
                    intended_display TEXT NOT NULL,
                    actual_display TEXT NOT NULL,
                    verification_message TEXT NOT NULL,
                    error_details TEXT NULL,
                    rolled_back_at INTEGER NULL
                );
                CREATE UNIQUE INDEX IF NOT EXISTS ix_transactions_seq ON transactions(seq);
                CREATE INDEX IF NOT EXISTS ix_transactions_batch ON transactions(batch_id);
                CREATE INDEX IF NOT EXISTS ix_transactions_status ON transactions(status);

                CREATE TABLE IF NOT EXISTS metrics (
                    timestamp INTEGER NOT NULL,
                    metric INTEGER NOT NULL,
                    value REAL NOT NULL,
                    PRIMARY KEY (metric, timestamp)
                ) WITHOUT ROWID;

                CREATE TABLE IF NOT EXISTS documents (
                    collection TEXT NOT NULL,
                    id TEXT NOT NULL,
                    timestamp INTEGER NOT NULL,
                    json TEXT NOT NULL,
                    PRIMARY KEY (collection, id)
                ) WITHOUT ROWID;
                CREATE INDEX IF NOT EXISTS ix_documents_time ON documents(collection, timestamp);

                PRAGMA user_version = 1;
                """;
            create.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    /// <summary>Löscht sämtliche von der Anwendung erzeugten Daten (Einstellungen → „Anwendungsdaten entfernen“).</summary>
    public void DeleteAllData()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM transactions; DELETE FROM metrics; DELETE FROM documents;";
        command.ExecuteNonQuery();
        using var vacuum = connection.CreateCommand();
        vacuum.CommandText = "VACUUM;";
        vacuum.ExecuteNonQuery();
    }
}
