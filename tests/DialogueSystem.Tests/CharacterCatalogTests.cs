using DialogueSystem.Application;
using DialogueSystem.Domain;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class CharacterCatalogTests
{
    [Fact]
    public async Task FindsUsesRenamesTheSpeakerAndProtectsReferencedCharacters()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        const string characters = "/api/characters";
        const string dialogues = "/api/dialogues";
        using var publicPage = await guest.Send(HttpMethod.Get, "/characters");
        Assert.Equal(OK, publicPage.StatusCode);
        Assert.Contains("id=\"character-picker\"", await publicPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        await editor.Login("editor", password);

        var list = await editor.GetAll<CharacterSummary>(characters);
        var marta = Assert.Single(list, character => character.Name == "Марта");
        var terra = Assert.Single(list, character => character.Name == "Терра");
        var martaRoute = $"{characters}/{marta.Id}";
        var archive = Assert.Single(await editor.GetDialogues(dialogues),
            item => item.Title == "Ключ от архива");
        var draft = await editor.Read<DialogueSummary>(
            HttpMethod.Post, dialogues, Created, new { title = "У кабинета" });
        var draftRoute = $"{dialogues}/{draft.Id}";
        var line = await editor.Read<LineSummary>(
            HttpMethod.Post, draftRoute + "/lines", Created, new { speakerId = marta.Id, text = "Подождите здесь." });

        await guest.Expect(Unauthorized, HttpMethod.Get, martaRoute + "/usage");
        await guest.Expect(Unauthorized, HttpMethod.Delete, martaRoute);
        await editor.Expect(BadRequest, HttpMethod.Delete, martaRoute, includeToken: false);
        await editor.Expect(Conflict, HttpMethod.Delete, martaRoute,
            ifMatch: (await editor.Get<CharacterSummary>(martaRoute)).Version);

        var usage = await editor.Get<CharacterUsage>(martaRoute + "/usage");
        Assert.Equal(6, usage.LineCount);
        Assert.Equal(2, usage.Dialogues.Count);
        var archiveUsage = Assert.Single(usage.Dialogues, item => item.DialogueId == archive.Id);
        Assert.Equal(5, archiveUsage.LineCount);
        Assert.Equal(archive.StartLineId, archiveUsage.FirstLineId);
        Assert.Equal(new CharacterDialogueUsage(draft.Id, draft.Title, line.Id, 1),
            Assert.Single(usage.Dialogues, item => item.DialogueId == draft.Id));

        await editor.Expect(NoContent, HttpMethod.Put, martaRoute,
            new { name = " Марта Соколова ", version = (await editor.Get<CharacterSummary>(martaRoute)).Version });
        var context = await editor.Get<LineContext>($"{draftRoute}/lines/{line.Id}/context");
        Assert.Equal(marta.Id, context.Line.SpeakerId);
        Assert.Equal("Марта Соколова", context.Line.Speaker);
        Assert.Equal("Марта Соколова", (await editor.Get<CharacterUsage>(martaRoute + "/usage")).Name);

        var temporary = await editor.Read<Character>(
            HttpMethod.Post, characters, Created, new { name = "Дежурный" });
        var temporaryRoute = $"{characters}/{temporary.Id}";
        var lineRoute = $"{draftRoute}/lines/{line.Id}";
        await editor.Expect(NoContent, HttpMethod.Put, lineRoute,
            new { speakerId = temporary.Id, text = line.Text, version = (await editor.Get<LineSummary>(lineRoute)).Version });
        Assert.Equal(1, (await editor.Get<CharacterUsage>(temporaryRoute + "/usage")).LineCount);
        await editor.Expect(Conflict, HttpMethod.Delete, temporaryRoute,
            ifMatch: (await editor.Get<CharacterSummary>(temporaryRoute)).Version);

        await editor.Expect(NoContent, HttpMethod.Put, lineRoute,
            new { speakerId = terra.Id, text = line.Text, version = (await editor.Get<LineSummary>(lineRoute)).Version });
        var unused = await editor.Get<CharacterUsage>(temporaryRoute + "/usage");
        Assert.Equal(0, unused.LineCount);
        Assert.Empty(unused.Dialogues);
        await editor.Expect(NoContent, HttpMethod.Delete, temporaryRoute,
            ifMatch: (await editor.Get<CharacterSummary>(temporaryRoute)).Version);
        await editor.Expect(NotFound, HttpMethod.Delete, temporaryRoute,
            ifMatch: new string('0', 64));
        await editor.Expect(NotFound, HttpMethod.Get, temporaryRoute + "/usage");
        Assert.Equal(terra.Id, (await editor.Get<LineContext>(lineRoute + "/context")).Line.SpeakerId);
    }

    [Fact]
    public async Task ReassignsOnlyTheRequestedLinesAndKeepsSavedSnapshots()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        const string characters = "/api/characters";
        const string dialogues = "/api/dialogues";
        await editor.Login("editor", password);

        var cast = await editor.GetAll<CharacterSummary>(characters);
        var marta = Assert.Single(cast, character => character.Name == "Марта");
        var terra = Assert.Single(cast, character => character.Name == "Терра");
        var archive = Assert.Single(await editor.GetDialogues(dialogues),
            dialogue => dialogue.Title == "Ключ от архива");
        var archiveRoute = $"{dialogues}/{archive.Id}";
        var original = await editor.Get<DialogueDocument>(archiveRoute + "/export");
        var preview = await editor.Read<PreviewStep>(HttpMethod.Post, archiveRoute + "/previews");
        var historyRoute = $"{archiveRoute}/previews/{preview.PreviewId}/history";
        var revision = await editor.Read<DialogueRevisionSummary>(
            HttpMethod.Post, archiveRoute + "/revisions", Created, new { label = "С исходным говорящим" });

        var second = await editor.Read<DialogueSummary>(
            HttpMethod.Post, dialogues, Created, new { title = "Второй сценарий" });
        var line = await editor.Read<LineSummary>(HttpMethod.Post, $"{dialogues}/{second.Id}/lines",
            Created, new { speakerId = marta.Id, text = "Я всё ещё здесь." });
        var secondContext = $"{dialogues}/{second.Id}/lines/{line.Id}/context";
        var route = $"{characters}/{marta.Id}/reassign";
        var input = new { targetCharacterId = terra.Id, dialogueId = archive.Id,
            version = (await editor.Get<CharacterSummary>($"{characters}/{marta.Id}")).Version,
            targetVersion = (await editor.Get<CharacterSummary>($"{characters}/{terra.Id}")).Version };

        await guest.Expect(Unauthorized, HttpMethod.Post, route, input);
        await editor.Expect(BadRequest, HttpMethod.Post, route, input, includeToken: false);
        await editor.Expect(BadRequest, HttpMethod.Post, route, input with { targetCharacterId = marta.Id, targetVersion = input.version });
        await editor.Expect(NotFound, HttpMethod.Post, route, input with { targetCharacterId = int.MaxValue });
        await editor.Expect(NotFound, HttpMethod.Post, route, input with { dialogueId = int.MaxValue });
        await editor.Expect(NotFound, HttpMethod.Post, $"{characters}/{int.MaxValue}/reassign", input);
        Assert.Equal(6, (await editor.Get<CharacterUsage>($"{characters}/{marta.Id}/usage")).LineCount);

        var scoped = await editor.Read<ReassignedLines>(HttpMethod.Post, route, payload: input);
        Assert.Equal(5, scoped.UpdatedLineCount);
        var edited = await editor.Get<DialogueDocument>(archiveRoute + "/export");
        Assert.Equal(original.StartLineId, edited.StartLineId);
        Assert.Equal(original.Lines.Select(item => item with { Speaker = terra.Name, SpeakerKey = terra.Key }), edited.Lines);
        Assert.Equal(original.Choices.ToArray(), edited.Choices.ToArray());
        Assert.Equal(marta.Id, (await editor.Get<LineContext>(secondContext)).Line.SpeakerId);
        Assert.Equal(0, (await editor.Read<ReassignedLines>(HttpMethod.Post, route, payload: input with {
            version = (await editor.Get<CharacterSummary>($"{characters}/{marta.Id}")).Version,
            targetVersion = (await editor.Get<CharacterSummary>($"{characters}/{terra.Id}")).Version })).UpdatedLineCount);
        await editor.Expect(Conflict, HttpMethod.Delete, $"{characters}/{marta.Id}",
            ifMatch: (await editor.Get<CharacterSummary>($"{characters}/{marta.Id}")).Version);

        var previous = await editor.Get<DialogueDocument>($"{archiveRoute}/revisions/{revision.Id}/export");
        Assert.Equal(original.Lines.ToArray(), previous.Lines.ToArray());
        var history = await editor.Get<PreviewHistory>(historyRoute);
        Assert.Equal(marta.Name, Assert.Single(history.Steps).Speaker);

        var remaining = await editor.Read<ReassignedLines>(HttpMethod.Post, route, payload: new { targetCharacterId = terra.Id,
            version = (await editor.Get<CharacterSummary>($"{characters}/{marta.Id}")).Version,
            targetVersion = (await editor.Get<CharacterSummary>($"{characters}/{terra.Id}")).Version });
        Assert.Equal(1, remaining.UpdatedLineCount);
        Assert.Equal(terra.Id, (await editor.Get<LineContext>(secondContext)).Line.SpeakerId);
        await editor.Expect(NoContent, HttpMethod.Delete, $"{characters}/{marta.Id}",
            ifMatch: (await editor.Get<CharacterSummary>($"{characters}/{marta.Id}")).Version);
        Assert.Equal(marta.Name, Assert.Single((await editor.Get<PreviewHistory>(historyRoute)).Steps).Speaker);
    }
}
