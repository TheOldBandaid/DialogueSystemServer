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
    }
}
