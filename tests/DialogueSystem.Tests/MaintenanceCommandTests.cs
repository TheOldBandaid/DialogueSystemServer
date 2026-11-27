using System.Diagnostics;
using DialogueSystem.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Tests;

public sealed class MaintenanceCommandTests
{
    [Fact]
    public async Task InvalidRestoreReturnsAnErrorWithoutAStackTraceOrChangesToTheDatabase()
    {
        using var data = new DialogueTestData();
        string epoch;
        await using (var app = data.Start()) epoch = app.Services.GetRequiredService<SessionEpoch>().Value;
        using (var pooled = new SqliteConnection(new SqliteConnectionStringBuilder
                   { DataSource = data.DatabasePath, ForeignKeys = true }.ToString())) SqliteConnection.ClearPool(pooled);
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        var source = Path.Combine(directory, "unrelated.db");
        await using (var connection = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE Other (Id INTEGER PRIMARY KEY);";
            await command.ExecuteNonQueryAsync();
        }
        var database = await File.ReadAllBytesAsync(data.DatabasePath);
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { typeof(Program).Assembly.Location, "--maintenance", "restore", "--file", source,
                     "--DialogueSystem:DataDirectory", directory }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Файл не является резервной копией базы диалогов.", await error);
        Assert.DoesNotContain("InvalidDataException", await error);
        Assert.DoesNotContain("MaintenanceCommands", await error);
        Assert.DoesNotContain("База восстановлена", await output);
        Assert.Equal(database, await File.ReadAllBytesAsync(data.DatabasePath));
        Assert.Equal(epoch, await File.ReadAllTextAsync(Path.Combine(directory, "session-epoch")));
    }
}
