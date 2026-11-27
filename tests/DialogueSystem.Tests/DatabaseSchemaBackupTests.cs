using System.Text.RegularExpressions;
using DialogueSystem.Infrastructure;
using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Tests;

public sealed class DatabaseSchemaBackupTests
{
    [Theory]
    [InlineData("foreign-key", false)]
    [InlineData("nullable-column", false)]
    [InlineData("unique-key", false)]
    [InlineData("foreign-key-order", true)]
    public async Task ValidatesTableConstraintsBeforeRestoringWithoutDependingOnDeclarationOrder(string change, bool compatible)
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        var backups = app.Services.GetRequiredService<DatabaseBackups>();
        var backup = await backups.CreateAsync(BackupKind.Manual);
        var epoch = app.Services.GetRequiredService<SessionEpoch>().Value;
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var titles = await db.Dialogues.OrderBy(dialogue => dialogue.Id).Select(dialogue => dialogue.Title).ToArrayAsync();
        await RebuildTable(backup.Path, change);
        if (compatible)
        {
            await DatabaseBackups.ValidateAsync(backup.Path);
            return;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackups.ValidateAsync(backup.Path));
        await Assert.ThrowsAsync<InvalidDataException>(() => backups.RestoreAsync(backup.Path));
        Assert.Equal(titles, await db.Dialogues.OrderBy(dialogue => dialogue.Id).Select(dialogue => dialogue.Title).ToArrayAsync());
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        Assert.Equal(epoch, await File.ReadAllTextAsync(Path.Combine(directory, "session-epoch")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(directory, "backups"), "before-restore-*"));
        Assert.Empty(Directory.GetFiles(directory, "*.partial*"));
    }

    private static async Task RebuildTable(string path, string change)
    {
        var table = change == "unique-key" ? "Lines" : "Choices";
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false, ForeignKeys = false }.ToString());
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", table);
        var original = (string)(await command.ExecuteScalarAsync())!;
        var sql = change switch
        {
            "foreign-key" => Regex.Replace(original,
                """,\s*CONSTRAINT "FK_Choices_Lines_DialogueId_TargetLineId" FOREIGN KEY \([^)]+\) REFERENCES "Lines" \([^)]+\) ON DELETE RESTRICT""", ""),
            "nullable-column" => original.Replace("\"Text\" TEXT NOT NULL", "\"Text\" TEXT NULL", StringComparison.Ordinal),
            "unique-key" => Regex.Replace(original, """,\s*CONSTRAINT "AK_Lines_DialogueId_Id" UNIQUE \([^)]+\)""", ""),
            "foreign-key-order" => ReverseForeignKeys(original),
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        Assert.NotEqual(original, sql);
        var objects = new List<string>();
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE tbl_name=$name AND type IN ('index','trigger') AND sql IS NOT NULL ORDER BY type, name";
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) objects.Add(reader.GetString(0));
        command.Parameters.Clear();
        command.CommandText = "PRAGMA legacy_alter_table=ON";
        await command.ExecuteNonQueryAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        command.Transaction = transaction;
        command.CommandText = sql.Replace($"CREATE TABLE \"{table}\"", "CREATE TABLE \"BackupTable\"", StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE \"BackupTable\"", command.CommandText, StringComparison.Ordinal);
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"INSERT INTO BackupTable SELECT * FROM \"{table}\";";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"DROP TABLE \"{table}\";";
        await command.ExecuteNonQueryAsync();
        command.CommandText = $"ALTER TABLE BackupTable RENAME TO \"{table}\";";
        await command.ExecuteNonQueryAsync();
        foreach (var definition in objects)
        {
            command.CommandText = definition;
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
        command.Transaction = null;
        command.CommandText = "PRAGMA integrity_check";
        Assert.Equal("ok", await command.ExecuteScalarAsync());
    }

    private static string ReverseForeignKeys(string sql)
    {
        var constraints = Regex.Matches(sql,
            """CONSTRAINT "FK_Choices_[^"]+" FOREIGN KEY \([^)]+\) REFERENCES "Lines" \([^)]+\) ON DELETE (?:CASCADE|RESTRICT)""");
        Assert.Equal(2, constraints.Count);
        return sql.Replace(constraints[0].Value, "BACKUP_CONSTRAINT", StringComparison.Ordinal)
            .Replace(constraints[1].Value, constraints[0].Value, StringComparison.Ordinal)
            .Replace("BACKUP_CONSTRAINT", constraints[1].Value, StringComparison.Ordinal);
    }
}
