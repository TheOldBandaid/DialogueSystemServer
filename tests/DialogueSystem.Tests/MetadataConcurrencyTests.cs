using DialogueSystem.Application;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class MetadataConcurrencyTests
{
    [Fact]
    public async Task NewHistoryOrRevisionInvalidatesDeletionWithoutInvalidatingGraphMetadata()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var before = (await client.GetDialogues("/api/dialogues"))[0];
        var path = $"/api/dialogues/{before.Id}";
        await client.Read<PreviewStep>(HttpMethod.Post, path + "/previews");
        var after = await client.Get<DialogueSummary>(path);
        Assert.Equal(before.Version, after.Version);
        Assert.NotEqual(before.DeleteVersion, after.DeleteVersion);
        using var stale = await client.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: before.DeleteVersion);
        Assert.Equal(PreconditionFailed, stale.StatusCode);
        await client.Read<DialogueRevisionSummary>(HttpMethod.Post, path + "/revisions", Created, new { label = "Новая версия" });
        using var staleRevision = await client.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: after.DeleteVersion);
        Assert.Equal(PreconditionFailed, staleRevision.StatusCode);
    }

    [Fact]
    public async Task MetadataAndDialogueDeletionRejectAnOutdatedVersion()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var a = new DialogueApiClient(app);
        using var b = new DialogueApiClient(app);
        await a.Login("editor", password);
        await b.Login("editor", password);
        var original = (await a.GetDialogues("/api/dialogues"))[0];
        var path = $"/api/dialogues/{original.Id}";
        await a.Expect(NoContent, HttpMethod.Put, path, new { title = "Свежие данные", original.Version });
        await b.Expect(Conflict, HttpMethod.Put, path + "", new { minimumPlayerLevel = 9, original.Version });
        await b.Expect(Conflict, HttpMethod.Put, path + "", new { startLineId = (int?)null, original.Version });
        using var staleDelete = await b.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: original.DeleteVersion);
        Assert.Equal(PreconditionFailed, staleDelete.StatusCode);
        var fresh = await b.Get<DialogueSummary>(path);
        Assert.Equal("Свежие данные", fresh.Title);
        Assert.Equal(original.MinimumPlayerLevel, fresh.MinimumPlayerLevel);
        Assert.Equal(original.StartLineId, fresh.StartLineId);
        using var noVersion = await b.Send(HttpMethod.Delete, path, includeVersion: false);
        Assert.Equal(428, (int)noVersion.StatusCode);
        using var missing = await b.Send(HttpMethod.Put, path,
            new { title = "Без версии", fresh.StartLineId, fresh.MinimumPlayerLevel }, includeVersion: false);
        Assert.Equal(428, (int)missing.StatusCode);
        using var wildcard = await b.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: "*");
        Assert.Equal(BadRequest, wildcard.StatusCode);
    }

    [Fact]
    public async Task DeletingALineOrChoiceRejectsUnseenChangesToContentOrLinks()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var dialogue = (await client.GetDialogues("/api/dialogues"))[0];
        var route = $"/api/dialogues/{dialogue.Id}";
        var speaker = (await client.GetAll<CharacterSummary>("/api/characters"))[0];
        var line = await client.Read<LineSummary>(HttpMethod.Post, route + "/lines", Created,
            new { speakerId = speaker.Id, text = "Черновик" });
        var path = route + $"/lines/{line.Id}";
        var original = await client.Get<LineContext>(path + "/context");
        var choice = await client.Read<System.Text.Json.Nodes.JsonObject>(HttpMethod.Post, path + "/choices", Created,
            new { targetLineId = dialogue.StartLineId, text = "Новая связь", position = 1 });
        using var stale = await client.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: original.DeleteVersion);
        Assert.Equal(PreconditionFailed, stale.StatusCode);
        var context = await client.Get<LineContext>(path + "/context");
        var answer = Assert.Single(context.Outgoing);
        var choicePath = $"{path}/choices/{answer.Id}";
        await client.Expect(NoContent, HttpMethod.Put, choicePath,
            new { targetLineId = answer.Line.Id, text = "Обновлённый ответ", answer.Position, answer.Version });
        using var staleChoice = await client.Send(HttpMethod.Delete, choicePath, includeVersion: false, ifMatch: answer.Version);
        Assert.Equal(PreconditionFailed, staleChoice.StatusCode);
        Assert.Equal("Обновлённый ответ", Assert.Single((await client.Get<LineContext>(path + "/context")).Outgoing).Text);
        await client.Expect(NoContent, HttpMethod.Delete, path);
    }

    [Fact]
    public async Task CharacterDeletionAndReassignmentRejectRenamedOrChangedUsages()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var source = await client.Read<CharacterSummary>(HttpMethod.Post, "/api/characters", Created, new { name = "Источник" });
        var target = await client.Read<CharacterSummary>(HttpMethod.Post, "/api/characters", Created, new { name = "Цель" });
        var path = $"/api/characters/{source.Id}";
        await client.Expect(NoContent, HttpMethod.Put, path, new { name = "Новое имя", source.Version });
        using var staleDelete = await client.Send(HttpMethod.Delete, path, includeVersion: false, ifMatch: source.Version);
        Assert.Equal(PreconditionFailed, staleDelete.StatusCode);
        var fresh = await client.Get<CharacterSummary>(path);
        var dialogue = (await client.GetDialogues("/api/dialogues"))[0];
        var line = await client.Read<LineSummary>(HttpMethod.Post, $"/api/dialogues/{dialogue.Id}/lines", Created,
            new { speakerId = source.Id, text = "Добавлено другим редактором" });
        using var staleReassign = await client.Send(HttpMethod.Post, path + "/reassign",
            new { targetCharacterId = target.Id, version = fresh.Version, targetVersion = target.Version }, includeVersion: false);
        Assert.Equal(Conflict, staleReassign.StatusCode);
        Assert.Equal(source.Id, (await client.Get<LineContext>($"/api/dialogues/{dialogue.Id}/lines/{line.Id}/context")).Line.SpeakerId);
    }
}
