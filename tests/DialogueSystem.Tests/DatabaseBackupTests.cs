using System.Net;
using System.Net.Http.Json;
using DialogueSystem.Application;
using DialogueSystem.Infrastructure;
using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Tests;

public sealed class DatabaseBackupTests
{
    [Fact]
    public async Task RestoresAccountsVersionsAndHistoryAndRevokesEvenMatchingOldSessionVersions()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        DatabaseBackup backup;
        string cookie, epoch, title;
        int dialogueId;
        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]))
        {
            using var client = app.CreateClient();
            var csrf = await client.GetFromJsonAsync<Csrf>("/api/accounts/csrf");
            using var login = new HttpRequestMessage(HttpMethod.Post, "/api/accounts/login")
            {
                Content = JsonContent.Create(new { login = "editor", password })
            };
            login.Headers.Add("X-CSRF-TOKEN", csrf!.Token);
            using var response = await client.SendAsync(login);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith("DialogueSystem.Session=", StringComparison.Ordinal)).Split(';')[0];
            using var editor = new DialogueApiClient(app);
            await editor.Login("editor", password);
            var dialogue = (await editor.Get<DialogueSummary[]>("/api/dialogues"))[0];
            dialogueId = dialogue.Id;
            title = dialogue.Title;
            await editor.Read<PreviewStep>(HttpMethod.Post, $"/api/dialogues/{dialogueId}/previews");
            await editor.Read<DialogueRevisionSummary>(HttpMethod.Post, $"/api/dialogues/{dialogueId}/revisions",
                HttpStatusCode.Created, new { label = "До изменений" });
            epoch = app.Services.GetRequiredService<SessionEpoch>().Value;
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
            backup = await app.Services.GetRequiredService<DatabaseBackups>().CreateAsync(BackupKind.Manual);
            await db.Dialogues.Where(item => item.Id == dialogueId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Title, "После копии"));
            await editor.Expect(HttpStatusCode.NoContent, HttpMethod.Put, "/api/accounts/me/password",
                new { currentPassword = password, newPassword = Guid.NewGuid().ToString("N") });
        }
        using (var lease = new DataDirectoryLease(directory))
        using (var backups = new DatabaseBackups(directory))
        {
            var result = await backups.RestoreAsync(backup.Path);
            Assert.True(File.Exists(Path.Combine(result.PreservedDirectory, "dialogues.db")));
            Assert.NotEqual(epoch, new SessionEpoch(directory).Value);
        }
        await using var restored = data.Start();
        using var restoredScope = restored.Services.CreateScope();
        var restoredDb = restoredScope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        Assert.Equal(title, await restoredDb.Dialogues.Where(item => item.Id == dialogueId).Select(item => item.Title).SingleAsync());
        Assert.Equal(1, await restoredDb.DialogueRevisions.CountAsync());
        Assert.Equal(1, await restoredDb.Set<DialogueSystem.Domain.PreviewHistoryEntry>().CountAsync());
        using var old = restored.CreateClient(new() { HandleCookies = false });
        old.DefaultRequestHeaders.Add("Cookie", cookie);
        using var rejected = await old.GetAsync("/api/accounts/me");
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var fresh = new DialogueApiClient(restored);
        await fresh.Login("editor", password);
    }

    [Fact]
    public async Task InvalidRestoreDoesNotChangeTheLiveDatabaseOrSessionMarker()
    {
        using var data = new DialogueTestData();
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        await using var app = data.Start();
        var epoch = app.Services.GetRequiredService<SessionEpoch>().Value;
        var path = Path.Combine(directory, "unrelated.db");
        await using (var connection = new SqliteConnection($"Data Source={path};Pooling=False")) await connection.OpenAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var before = await db.Dialogues.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Title).ToArrayAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => app.Services.GetRequiredService<DatabaseBackups>().RestoreAsync(path));
        Assert.Equal(before, await db.Dialogues.AsNoTracking().OrderBy(item => item.Id).Select(item => item.Title).ToArrayAsync());
        Assert.Equal(epoch, File.ReadAllText(Path.Combine(directory, "session-epoch")));
    }

    [Fact]
    public async Task RestoresAnOlderSchemaOnlyAfterUpgradingItsTemporaryCopy()
    {
        using var data = new DialogueTestData();
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        await using (var app = data.Start()) using (var client = app.CreateClient()) { }
        var source = Path.Combine(directory, "older-schema.db");
        const string initial = "20261120110136_InitialDialogueSchema";
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite($"Data Source={source};Pooling=False").Options;
        await using (var db = new DialogueDbContext(options))
        {
            await db.Database.MigrateAsync(initial);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Dialogues (Title) VALUES ({"Из старой копии"})");
        }
        var originalFile = await File.ReadAllBytesAsync(source);
        using (var lease = new DataDirectoryLease(directory))
        using (var backups = new DatabaseBackups(directory)) await backups.RestoreAsync(source);
        Assert.Equal(originalFile, await File.ReadAllBytesAsync(source));
        await using (var unchanged = new DialogueDbContext(options))
            Assert.Equal(new[] { initial }, await unchanged.Database.GetAppliedMigrationsAsync());
        await using var restored = data.Start();
        using var scope = restored.Services.CreateScope();
        var current = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        Assert.Empty(await current.Database.GetPendingMigrationsAsync());
        Assert.Equal("Из старой копии", (await current.Dialogues.SingleAsync()).Title);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RejectsMatchingTableNamesWithAnEmptyOrFalseMigrationHistory(int claimedSchema)
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        var directory = Path.GetDirectoryName(data.DatabasePath)!;
        var source = Path.Combine(directory, "incorrect-schema.db");
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var titles = await db.Dialogues.OrderBy(item => item.Id).Select(item => item.Title).ToArrayAsync();
        var epoch = app.Services.GetRequiredService<SessionEpoch>().Value;
        await using (var connection = new SqliteConnection($"Data Source={source};Pooling=False"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Dialogues (Id INTEGER PRIMARY KEY);
                CREATE TABLE Characters (Id INTEGER PRIMARY KEY);
                CREATE TABLE Lines (Id INTEGER PRIMARY KEY);
                CREATE TABLE Choices (Id INTEGER PRIMARY KEY);
                CREATE TABLE __EFMigrationsHistory (MigrationId TEXT NOT NULL PRIMARY KEY, ProductVersion TEXT NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
            if (claimedSchema != 0)
            {
                command.CommandText = "INSERT INTO __EFMigrationsHistory VALUES ($id, '10.0.12');";
                var parameter = command.Parameters.Add("$id", SqliteType.Text);
                var migrations = db.Database.GetMigrations();
                foreach (var migration in claimedSchema == 1 ? migrations.Take(1) : migrations)
                {
                    parameter.Value = migration;
                    await command.ExecuteNonQueryAsync();
                }
            }
        }
        if (claimedSchema != 1)
            await Assert.ThrowsAsync<InvalidDataException>(() => DatabaseBackups.ValidateAsync(source));
        await Assert.ThrowsAsync<InvalidDataException>(() => app.Services.GetRequiredService<DatabaseBackups>().RestoreAsync(source));
        Assert.Equal(titles, await db.Dialogues.OrderBy(item => item.Id).Select(item => item.Title).ToArrayAsync());
        Assert.Equal(epoch, File.ReadAllText(Path.Combine(directory, "session-epoch")));
        Assert.Empty(Directory.GetDirectories(directory, "before-restore-*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(directory, "*.partial*"));
    }

    [Fact]
    public async Task DailyBackupIsIdempotentAndAutomaticRetentionKeepsManualCopies()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:Backups:Enabled", "true")]);
        var backups = app.Services.GetRequiredService<DatabaseBackups>();
        var root = Path.Combine(Path.GetDirectoryName(data.DatabasePath)!, "backups");
        Assert.Single(Directory.GetFiles(root, "dialogues-daily-*.db"));
        await backups.EnsureDailyAsync();
        Assert.Single(Directory.GetFiles(root, "dialogues-daily-*.db"));
        var manual = await backups.CreateAsync(BackupKind.Manual);
        for (var i = 0; i < 8; i++) await backups.CreateAsync(BackupKind.Daily);
        for (var i = 0; i < 4; i++) await backups.CreateAsync(BackupKind.Migration);
        for (var i = 0; i < 4; i++) await backups.CreateAsync(BackupKind.Cleanup);
        Assert.Equal(7, Directory.GetFiles(root, "dialogues-daily-*.db").Length);
        Assert.Equal(3, Directory.GetFiles(root, "dialogues-migration-*.db").Length);
        Assert.Equal(3, Directory.GetFiles(root, "dialogues-cleanup-*.db").Length);
        Assert.True(File.Exists(manual.Path));
        await DatabaseBackups.ValidateAsync(manual.Path);
        Assert.Empty(Directory.GetFiles(root, "*.partial*"));
        Assert.Empty(Directory.GetFiles(root, "*.db-wal"));
        Assert.Empty(Directory.GetFiles(root, "*.db-shm"));
    }

    private sealed record Csrf(string Token);
}
