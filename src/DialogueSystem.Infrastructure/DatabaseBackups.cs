using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure;

public enum BackupKind { Daily, Migration, Manual, Recovery }
public sealed record DatabaseBackup(string Path, BackupKind Kind, long Size);
public sealed record DatabaseRestore(string PreservedDirectory);

public sealed class DatabaseBackups(string directory, TimeProvider? clock = null) : IDisposable
{
    private readonly string dataDirectory = System.IO.Path.GetFullPath(directory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private string DatabasePath => System.IO.Path.Combine(dataDirectory, "dialogues.db");
    private string BackupDirectory => System.IO.Path.Combine(dataDirectory, "backups");

    public async Task<DatabaseBackup> CreateAsync(BackupKind kind, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try { return await CreateCoreAsync(kind, ct); }
        finally { gate.Release(); }
    }

    public async Task EnsureDailyAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var prefix = $"dialogues-daily-{time.GetUtcNow():yyyyMMdd}-";
            if (Directory.Exists(BackupDirectory) && Directory.EnumerateFiles(BackupDirectory, prefix + "*.db").Any()) return;
            await CreateCoreAsync(BackupKind.Daily, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<DatabaseBackup> CreateCoreAsync(BackupKind kind, CancellationToken ct)
    {
        if (!File.Exists(DatabasePath)) throw new FileNotFoundException("Рабочая база не найдена.");
        Directory.CreateDirectory(BackupDirectory);
        var name = $"dialogues-{kind.ToString().ToLowerInvariant()}-{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.db";
        var path = System.IO.Path.Combine(BackupDirectory, name);
        var staging = path + ".partial";
        try
        {
            await CopyDatabaseAsync(DatabasePath, staging, ct);
            await ValidateAsync(staging, requireApplication: false, ct);
            File.Move(staging, path);
            Prune(BackupKind.Daily, 7);
            Prune(BackupKind.Migration, 3);
            return new(path, kind, new FileInfo(path).Length);
        }
        finally { DeleteFileSet(staging); }
    }

    public static async Task ValidateAsync(string path, bool requireApplication = true, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Резервная копия не найдена.", path);
        await using var connection = ReadConnection(path);
        await connection.OpenAsync(ct);
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(await check.ExecuteScalarAsync(ct) as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Резервная копия не прошла проверку целостности SQLite.");
        check.CommandText = "PRAGMA foreign_key_check;";
        await using (var reader = await check.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) throw new InvalidDataException("В резервной копии нарушены связи таблиц.");
        if (!requireApplication) return;
        check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Dialogues','Characters','Lines','Choices','__EFMigrationsHistory');";
        if ((long)(await check.ExecuteScalarAsync(ct))! != 5)
            throw new InvalidDataException("Файл не является резервной копией базы диалогов.");
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(connection).Options;
        await using var db = new DialogueDbContext(options);
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        if (applied.Except(db.Database.GetMigrations(), StringComparer.Ordinal).Any())
            throw new InvalidDataException("Копия создана другой версией схемы. Используйте соответствующую версию сервера.");
    }

    // The caller must hold DataDirectoryLease: restoration is a local, offline operation.
    public async Task<DatabaseRestore> RestoreAsync(string source, CancellationToken ct = default)
    {
        source = System.IO.Path.GetFullPath(source);
        if (string.Equals(source, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите резервную копию, а не рабочую базу.");
        await gate.WaitAsync(ct);
        var staging = System.IO.Path.Combine(dataDirectory, $"restore-{Guid.NewGuid():N}.partial");
        try
        {
            // Snapshot the source first, including committed WAL data, then validate the exact snapshot being restored.
            await CopyDatabaseAsync(source, staging, ct);
            await ValidateAsync(staging, requireApplication: true, ct);
            var preserved = Directory.CreateDirectory(System.IO.Path.Combine(BackupDirectory, $"before-restore-{Guid.NewGuid():N}"));
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(DatabasePath + suffix))
                    File.Copy(DatabasePath + suffix, System.IO.Path.Combine(preserved.FullName, "dialogues.db" + suffix));
            // A restored SessionVersion might equal an old cookie's version: invalidate through a marker outside the DB.
            SessionEpoch.Rotate(dataDirectory);
            await CopyDatabaseAsync(staging, DatabasePath, ct, standalone: false);
            return new(preserved.FullName);
        }
        finally
        {
            try { DeleteFileSet(staging); }
            finally { gate.Release(); }
        }
    }

    private void Prune(BackupKind kind, int keep)
    {
        var prefix = $"dialogues-{kind.ToString().ToLowerInvariant()}-";
        var candidates = Directory.EnumerateFiles(BackupDirectory, prefix + "*.db")
            .Where(path => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(path),
                $"\\A{prefix}[0-9]{{8}}-[0-9]{{6}}-[a-f0-9]{{32}}\\.db\\z"))
            .OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(path => path).Skip(keep);
        foreach (var path in candidates) DeleteFileSet(path);
    }

    private static void DeleteFileSet(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
    }

    private static SqliteConnection ReadConnection(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
    }.ToString());

    private static async Task CopyDatabaseAsync(string source, string destination, CancellationToken ct, bool standalone = true)
    {
        await using var input = ReadConnection(source);
        await input.OpenAsync(ct);
        await using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination, Pooling = false
        }.ToString());
        await output.OpenAsync(ct);
        ct.ThrowIfCancellationRequested();
        input.BackupDatabase(output);
        // Publish portable backups as a single file; preserve WAL for the working database.
        using var journal = output.CreateCommand();
        journal.CommandText = standalone ? "PRAGMA journal_mode=DELETE;" : "PRAGMA journal_mode=WAL;";
        await journal.ExecuteScalarAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() => gate.Dispose();
}
