using DialogueSystem.Infrastructure.Data;
using System.Globalization;
using System.Text.Json;
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
        if (requireApplication)
        {
            check.CommandText = "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name IN ('Dialogues','Characters','Lines','Choices','__EFMigrationsHistory');";
            if ((long)(await check.ExecuteScalarAsync(ct))! != 5)
                throw new InvalidDataException("Файл не является резервной копией базы диалогов.");
            var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(connection).Options;
            await using var db = new DialogueDbContext(options);
            var known = db.Database.GetMigrations().ToArray();
            var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
            if (applied.Length == 0 || !applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
                throw new InvalidDataException("В копии отсутствует совместимая последовательность миграций приложения.");
            if (applied.Length == known.Length)
            {
                try { await CheckSchemaAsync(db, ct); }
                catch (SqliteException error)
                {
                    throw new InvalidDataException("Схема резервной копии не соответствует миграциям приложения.", error);
                }
            }
        }
        check.CommandText = "PRAGMA foreign_key_check;";
        await using (var reader = await check.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct)) throw new InvalidDataException("В резервной копии нарушены связи таблиц.");
    }

    public async Task<DatabaseRestore> RestoreAsync(string source, CancellationToken ct = default)
    {
        source = System.IO.Path.GetFullPath(source);
        if (string.Equals(source, DatabasePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите резервную копию, а не рабочую базу.");
        await gate.WaitAsync(ct);
        var staging = System.IO.Path.Combine(dataDirectory, $"restore-{Guid.NewGuid():N}.partial");
        try
        {
            await CopyDatabaseAsync(source, staging, ct);
            await ValidateAsync(staging, requireApplication: true, ct);
            await PrepareRestoreAsync(staging, ct);
            var preserved = Directory.CreateDirectory(System.IO.Path.Combine(BackupDirectory, $"before-restore-{Guid.NewGuid():N}"));
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
                if (File.Exists(DatabasePath + suffix))
                    File.Copy(DatabasePath + suffix, System.IO.Path.Combine(preserved.FullName, "dialogues.db" + suffix));
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

    private static async Task PrepareRestoreAsync(string path, CancellationToken ct)
    {
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true };
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(connection.ToString()).Options;
        try
        {
            await using (var db = new DialogueDbContext(options)) await db.Database.MigrateAsync(ct);
            await ValidateAsync(path, requireApplication: true, ct);
        }
        catch (SqliteException error)
        {
            throw new InvalidDataException("Резервную копию нельзя подготовить к запуску. Рабочая база не изменена.", error);
        }
    }

    private static async Task CheckSchemaAsync(DialogueDbContext db, CancellationToken ct)
    {
        await db.Accounts.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Characters.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Dialogues.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Lines.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Choices.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Previews.AsNoTracking().Take(1).ToListAsync(ct);
        await db.DialogueRevisions.AsNoTracking().Take(1).ToListAsync(ct);
        await db.Set<DialogueSystem.Domain.PreviewHistoryEntry>().AsNoTracking().Take(1).ToListAsync(ct);
        await CheckSchemaObjectsAsync((SqliteConnection)db.Database.GetDbConnection(), ct);
    }

    private static async Task CheckSchemaObjectsAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using var reference = new SqliteConnection("Data Source=:memory:;Pooling=False;Foreign Keys=True");
        await reference.OpenAsync(ct);
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(reference).Options;
        await using var schema = new DialogueDbContext(options);
        await schema.Database.MigrateAsync(ct);
        var tables = schema.Model.GetEntityTypes().Select(entity => entity.GetTableName()!)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var expected = await ReadSchemaObjectsAsync(reference, tables, ct);
        var actual = await ReadSchemaObjectsAsync(connection, tables, ct);
        foreach (var (name, definition) in expected)
        {
            if (!actual.TryGetValue(name, out var current) || current != definition)
                throw new InvalidDataException($"В резервной копии отсутствует или изменён служебный объект {name}.");
        }
    }

    private static async Task<Dictionary<string, string>> ReadSchemaObjectsAsync(
        SqliteConnection connection, IReadOnlyList<string> tables, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name, sql FROM sqlite_schema WHERE type IN ('index', 'trigger') AND sql IS NOT NULL;";
        var objects = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var definition = string.Join(' ', reader.GetString(2).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');
                objects.Add($"{reader.GetString(0)} {reader.GetString(1)}", definition);
            }
        }
        foreach (var table in tables)
        {
            var columns = await ReadSchemaRowsAsync(connection,
                "SELECT name, type, \"notnull\", dflt_value, pk, hidden FROM pragma_table_xinfo($name) ORDER BY name;", table, ct);
            var foreignKeys = (await ReadSchemaRowsAsync(connection,
                "SELECT id, seq, \"table\", \"from\", \"to\", on_update, on_delete, match FROM pragma_foreign_key_list($name) ORDER BY id, seq;", table, ct))
                .GroupBy(row => row[0], StringComparer.Ordinal)
                .Select(group => JsonSerializer.Serialize(group.Select(row => row.Skip(1))))
                .Order(StringComparer.Ordinal).ToArray();
            var uniqueKeys = new List<string>();
            var indexes = await ReadSchemaRowsAsync(connection,
                "SELECT name, origin FROM pragma_index_list($name) WHERE origin IN ('pk', 'u');", table, ct);
            foreach (var index in indexes)
            {
                var parts = await ReadSchemaRowsAsync(connection,
                    "SELECT seqno, name, \"desc\", coll, \"key\" FROM pragma_index_xinfo($name) WHERE \"key\" = 1 ORDER BY seqno;", index[0]!, ct);
                uniqueKeys.Add(JsonSerializer.Serialize(new { origin = index[1], parts }));
            }
            objects.Add($"table {table}", JsonSerializer.Serialize(new
                { columns, foreignKeys, uniqueKeys = uniqueKeys.Order(StringComparer.Ordinal).ToArray() }));
        }
        return objects;
    }

    private static async Task<List<string?[]>> ReadSchemaRowsAsync(
        SqliteConnection connection, string sql, string name, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$name", name);
        var rows = new List<string?[]>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new string?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
                row[index] = reader.IsDBNull(index) ? null : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture);
            rows.Add(row);
        }
        return rows;
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
        using var journal = output.CreateCommand();
        journal.CommandText = standalone ? "PRAGMA journal_mode=DELETE;" : "PRAGMA journal_mode=WAL;";
        await journal.ExecuteScalarAsync(ct);
        ct.ThrowIfCancellationRequested();
    }

    public void Dispose() => gate.Dispose();
}
