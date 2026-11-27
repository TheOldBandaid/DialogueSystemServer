using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class CharacterIdentityTests
{
    [Fact]
    public async Task CopyAndRevisionKeepDifferentCharactersWithTheSameName()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var a = await editor.Read<Character>(HttpMethod.Post, "/api/characters", Created, new { name = "Тёзка" });
        var b = await editor.Read<Character>(HttpMethod.Post, "/api/characters", Created, new { name = "Тёзка" });
        var dialogue = await editor.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues", Created, new { title = "Разные персонажи" });
        var path = $"/api/dialogues/{dialogue.Id}";
        foreach (var speaker in new[] { a, b })
            await editor.Read<LineSummary>(HttpMethod.Post, path + "/lines", Created,
                new { speakerId = speaker.Id, text = $"Реплика {speaker.Id}", isEnding = true });
        var document = await editor.Get<DialogueDocument>(path + "/export");
        Assert.Equal(2, document.Version);
        Assert.Equal(2, document.Lines.Select(line => line.SpeakerKey).Distinct().Count());
        var copy = await editor.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues/import", Created, document);
        var copied = await editor.Get<LineSummary[]>($"/api/dialogues/{copy.Id}/lines");
        Assert.Equal(new[] { a.Id, b.Id }, copied.Select(line => line.SpeakerId));

        var revision = await editor.Read<DialogueRevisionSummary>(HttpMethod.Post, path + "/revisions", Created,
            new { label = "До переименования" });
        await editor.Expect(NoContent, HttpMethod.Put, $"/api/characters/{a.Id}", new { name = "Новое имя" });
        var restored = await editor.Read<DialogueSummary>(HttpMethod.Post,
            $"{path}/revisions/{revision.Id}/copy", Created);
        var lines = await editor.Get<LineSummary[]>($"/api/dialogues/{restored.Id}/lines");
        Assert.All(lines, line => Assert.Equal("Тёзка", line.Speaker));
        Assert.Equal(2, lines.Select(line => line.SpeakerId).Distinct().Count());
        Assert.Equal("Новое имя", (await editor.Get<Character[]>("/api/characters")).Single(c => c.Id == a.Id).Name);
        var count = (await editor.Get<Character[]>("/api/characters")).Length;
        var again = await editor.Read<DialogueSummary>(HttpMethod.Post,
            $"{path}/revisions/{revision.Id}/copy", Created);
        var repeated = await editor.Get<LineSummary[]>($"/api/dialogues/{again.Id}/lines");
        Assert.Equal(lines.Select(line => line.SpeakerId), repeated.Select(line => line.SpeakerId));
        Assert.Equal(count, (await editor.Get<Character[]>("/api/characters")).Length);
    }

    [Fact]
    public async Task IndependentCopiesKeepNamesakesDistinctWithoutRenamingOriginalSpeakers()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var a = await editor.Read<Character>(HttpMethod.Post, "/api/characters", Created, new { name = "Тёзка" });
        var b = await editor.Read<Character>(HttpMethod.Post, "/api/characters", Created, new { name = "Тёзка" });
        var dialogue = await editor.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues", Created, new { title = "Оригинал" });
        var path = $"/api/dialogues/{dialogue.Id}";
        foreach (var character in new[] { a, b })
            await editor.Read<LineSummary>(HttpMethod.Post, path + "/lines", Created,
                new { speakerId = character.Id, text = "Текст", isEnding = true });
        var input = new { cloneCharacters = true };
        await guest.Expect(Unauthorized, HttpMethod.Post, path + "/copy", input);
        await editor.Expect(BadRequest, HttpMethod.Post, path + "/copy", input, includeToken: false);
        var copy = await editor.Read<DialogueSummary>(HttpMethod.Post, path + "/copy", Created, input);
        var copied = await editor.Get<LineSummary[]>($"/api/dialogues/{copy.Id}/lines");
        Assert.Equal(2, copied.Select(line => line.SpeakerId).Distinct().Count());
        Assert.DoesNotContain(copied, line => line.SpeakerId == a.Id || line.SpeakerId == b.Id);
        var shared = await editor.Read<DialogueSummary>(HttpMethod.Post, path + "/copy", Created);
        var sharedLines = await editor.Get<LineSummary[]>($"/api/dialogues/{shared.Id}/lines");
        Assert.Equal(new[] { a.Id, b.Id }, sharedLines.Select(line => line.SpeakerId));
        await editor.Expect(NoContent, HttpMethod.Put, $"/api/characters/{copied[0].SpeakerId}", new { name = "Имя в копии" });
        Assert.All(await editor.Get<LineSummary[]>(path + "/lines"), line => Assert.Equal("Тёзка", line.Speaker));
        var revision = await editor.Read<DialogueRevisionSummary>(HttpMethod.Post, path + "/revisions", Created, new { label = "Снимок" });
        var restored = await editor.Read<DialogueSummary>(HttpMethod.Post, $"{path}/revisions/{revision.Id}/copy", Created, input);
        var restoredLines = await editor.Get<LineSummary[]>($"/api/dialogues/{restored.Id}/lines");
        Assert.Equal(2, restoredLines.Select(line => line.SpeakerId).Distinct().Count());
        Assert.DoesNotContain(restoredLines, line => line.SpeakerId == a.Id || line.SpeakerId == b.Id || copied.Any(c => c.SpeakerId == line.SpeakerId));
    }

    [Fact]
    public async Task MigrationAssignsDifferentKeysToExistingCharacters()
    {
        using var data = new DialogueTestData();
        var options = new DbContextOptionsBuilder<DialogueDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = data.DatabasePath, ForeignKeys = true }.ToString()).Options;
        await using (var old = new DialogueDbContext(options))
        {
            await old.Database.MigrateAsync("20261126134132_AddGraphRevision");
            await old.Database.ExecuteSqlRawAsync("INSERT INTO Characters (Name) VALUES ('Тёзка'), ('Тёзка');");
        }
        await using var app = data.Start(settings: [new("DialogueSystem:SeedDemo", "false")]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var keys = await db.Characters.Select(character => character.Key).ToArrayAsync();
        Assert.Equal(2, keys.Distinct().Count());
        Assert.All(keys, key => Assert.True(Guid.TryParseExact(key, "N", out _)));
        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(data.DatabasePath)!, "backups"), "*.db"));
        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await backup.OpenAsync();
        using var command = backup.CreateCommand();
        command.CommandText = "SELECT count(*) FROM Characters;";
        Assert.Equal(2L, await command.ExecuteScalarAsync());
        command.CommandText = "SELECT count(*) FROM pragma_table_info('Characters') WHERE name = 'Key';";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
    }
}
