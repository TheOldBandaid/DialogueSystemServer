using System.Text.Json;
using DialogueSystem.Infrastructure.Data;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueApiTests
{
    private readonly string editorPassword = Guid.NewGuid().ToString("N");
    private readonly string adminPassword = Guid.NewGuid().ToString("N");
    private const string DemoTitle = "Ключ от архива";

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public async Task ReachesBothEndingsAndAcceptsOnlyOneRequestPerStep(int firstChoice, int finalChoice)
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        using var admin = new DialogueApiClient(app);
        using var editorInAnotherSession = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);
        await admin.Login("admin", adminPassword);
        await editorInAnotherSession.Login("editor", editorPassword);

        var spec = await editor.Get<JsonElement>("/openapi/v1.json");
        Assert.True(spec.GetProperty("paths").TryGetProperty("/api/dialogues/{id}/lines/{lineId}/choices", out _));
        var demo = Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"), item => item.Title == DemoTitle);
        var path = $"/api/dialogues/{demo.Id}";
        var branch = Assert.Single(await editor.Get<LineSummary[]>($"{path}/story-branches"));
        var context = await editor.Get<LineContext>($"{path}/lines/{branch.Id}/context");
        Assert.Equal(2, context.Incoming.Count);
        Assert.Equal(2, context.Outgoing.Count);
        Assert.Empty((await editor.Get<DialogueValidation>($"{path}/validation")).Errors);

        var current = await editor.Read<PreviewStep>(HttpMethod.Post, $"{path}/previews");
        var advance = $"{path}/previews/{current.PreviewId}/choices";
        var selection = new SelectChoice(current.Choices[firstChoice].Id, current.Step);
        await admin.Expect(NotFound, HttpMethod.Post, advance, selection);
        await editor.Expect(BadRequest, HttpMethod.Post, advance, new SelectChoice(context.Outgoing[0].Id, current.Step));

        var replies = await Task.WhenAll(
            editor.Send(HttpMethod.Post, advance, selection),
            editorInAnotherSession.Send(HttpMethod.Post, advance, selection));
        using var firstReply = replies[0];
        using var secondReply = replies[1];
        Assert.Equal(new[] { OK, Conflict }, replies.Select(reply => reply.StatusCode).Order());
        current = await DialogueApiClient.ReadBody<PreviewStep>(Assert.Single(replies, reply => reply.StatusCode == OK));
        var history = await editor.Get<PreviewHistory>($"{path}/previews/{current.PreviewId}/history");
        Assert.Equal(1, history.CurrentStep);
        Assert.Equal(new[] { 0, 1 }, history.Steps.Select(step => step.Step));
        Assert.Equal(current.Line.Id, history.Steps[1].LineId);

        if (firstChoice == 0)
        {
            current = await editor.Read<PreviewStep>(HttpMethod.Post, advance,
                payload: new SelectChoice(Assert.Single(current.Choices).Id, current.Step));
        }

        Assert.Equal(branch.Id, current.Line.Id);
        var ending = await editor.Read<PreviewStep>(HttpMethod.Post, advance,
            payload: new SelectChoice(current.Choices[finalChoice].Id, current.Step));
        Assert.Equal(context.Outgoing[finalChoice].Line.Id, ending.Line.Id);
        Assert.True(ending.Line.IsEnding);
        Assert.Empty(ending.Choices);
    }

    [Fact]
    public async Task ValidatesDraftsAndAllowsRepair()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);
        var speakerId = Assert.Single(await editor.Get<Character[]>("/api/characters"), item => item.Name == "Марта").Id;
        var draft = await CreateDialogue("Архивный черновик");
        var other = await CreateDialogue("Другой диалог");
        var withoutStart = await editor.Read<DialogueValidation>(HttpMethod.Post, $"{draft}/previews", UnprocessableEntity);
        var missingStart = Assert.Single(withoutStart.Issues);
        Assert.Equal("missing-start", missingStart.Code);
        Assert.Null(missingStart.LineId);
        Assert.Equal(missingStart.Message, Assert.Single(withoutStart.Errors));

        var start = await AddLine(draft, "Начало");
        var loop = await AddLine(draft, "Цикл");
        var ending = await AddLine(draft, "Конец", isEnding: true);
        var foreign = await AddLine(other, "Чужая реплика", isEnding: true);

        await editor.Expect(BadRequest, HttpMethod.Put, $"{draft}/start", new { lineId = foreign });
        await editor.Expect(NoContent, HttpMethod.Put, $"{draft}/start", new { lineId = start });
        var first = await AddChoice(start, ending, 1);
        await AddChoice(start, loop, 2);
        await AddChoice(loop, loop, 1);

        var choices = $"{draft}/lines/{start}/choices";
        await editor.Expect(BadRequest, HttpMethod.Post, choices, Choice(foreign, 3));
        await editor.Expect(BadRequest, HttpMethod.Post, choices, Choice(int.MaxValue, 3));
        await editor.Expect(Conflict, HttpMethod.Post, choices, Choice(loop, 2));
        await editor.Expect(Conflict, HttpMethod.Post, $"{draft}/lines/{ending}/choices", Choice(loop, 1));
        await editor.Expect(BadRequest, HttpMethod.Put, $"{choices}/{first.Id}", Choice(foreign, 1));
        var unchanged = await editor.Get<LineContext>($"{draft}/lines/{start}/context");
        Assert.Equal(ending, unchanged.Outgoing[0].Line.Id);

        var invalid = await editor.Read<DialogueValidation>(HttpMethod.Post, $"{draft}/previews", UnprocessableEntity);
        var issue = Assert.Single(invalid.Issues);
        Assert.Equal("no-path-to-ending", issue.Code);
        Assert.Equal(loop, issue.LineId);
        Assert.Equal(issue.Message, Assert.Single(invalid.Errors));
        var report = await editor.Get<DialogueValidation>($"{draft}/validation");
        Assert.Equal(invalid.Issues.ToArray(), report.Issues.ToArray());
        await AddChoice(loop, ending, 2);
        await editor.Expect(OK, HttpMethod.Post, $"{draft}/previews");

        await editor.Expect(Conflict, HttpMethod.Delete, $"{draft}/lines/{ending}");
        await editor.Expect(Conflict, HttpMethod.Delete, $"{draft}/lines/{start}");
        var orphan = await AddLine(draft, "Неиспользуемая реплика");
        await AddChoice(orphan, orphan, 1);
        var validation = await editor.Get<DialogueValidation>($"{draft}/validation");
        Assert.Empty(validation.Issues);
        Assert.Contains(orphan, validation.UnreachableLineIds);
        await editor.Expect(NoContent, HttpMethod.Delete, $"{draft}/lines/{orphan}");
        await editor.Expect(NotFound, HttpMethod.Get, $"{draft}/lines/{orphan}/context");

        async Task<string> CreateDialogue(string title)
        {
            var created = await editor.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues", Created, new { title });
            return $"/api/dialogues/{created.Id}";
        }

        async Task<int> AddLine(string path, string text, bool isEnding = false) =>
            (await editor.Read<LineSummary>(HttpMethod.Post, $"{path}/lines", Created, new { speakerId, text, isEnding })).Id;

        Task<DialogueChoice> AddChoice(int source, int target, int position) =>
            editor.Read<DialogueChoice>(HttpMethod.Post, $"{draft}/lines/{source}/choices", Created, Choice(target, position));

        static object Choice(int targetLineId, int position) => new { text = "Продолжить", targetLineId, position };
    }

    [Fact]
    public async Task ShowsBothDirectionsOfSelfLoopAndOrdersContextChoices()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);

        var demo = Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"), item => item.Title == DemoTitle);
        var path = $"/api/dialogues/{demo.Id}";
        var branch = Assert.Single(await editor.Get<LineSummary[]>($"{path}/story-branches"));
        var startId = Assert.IsType<int>(demo.StartLineId);
        var contextPath = $"{path}/lines/{branch.Id}/context";
        var original = await editor.Get<LineContext>(contextPath);

        var extra = await editor.Read<DialogueChoice>(HttpMethod.Post, $"{path}/lines/{startId}/choices",
            Created, new { text = "Расскажите ещё", targetLineId = branch.Id, position = 3 });
        var loop = await editor.Read<DialogueChoice>(HttpMethod.Post, $"{path}/lines/{branch.Id}/choices",
            Created, new { text = "Повторить вопрос", targetLineId = branch.Id, position = 3 });
        var context = await editor.Get<LineContext>(contextPath);

        var expectedIncoming = original.Incoming.Select(choice => (Id: choice.Line.Id, choice.Position))
            .Append((Id: startId, extra.Position)).Append((Id: branch.Id, loop.Position))
            .OrderBy(choice => choice.Id).ThenBy(choice => choice.Position);
        Assert.Equal(expectedIncoming, context.Incoming.Select(choice => (choice.Line.Id, choice.Position)));
        Assert.Equal(new[] { 1, 2, 3 }, context.Outgoing.Select(choice => choice.Position));
        Assert.Equal(branch, Assert.Single(context.Incoming, choice => choice.Id == loop.Id).Line);
        Assert.Equal(branch, Assert.Single(context.Outgoing, choice => choice.Id == loop.Id).Line);
        await editor.Expect(NotFound, HttpMethod.Get, $"/api/dialogues/0/lines/{branch.Id}/context");
    }

    [Fact]
    public async Task RenamesAndDeletesOnlyTheSelectedDialogueAndItsHistory()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);

        const string dialoguesPath = "/api/dialogues";
        var dialogues = await editor.Get<DialogueSummary[]>(dialoguesPath);
        var original = Assert.Single(dialogues, item => item.Title == DemoTitle);
        var other = Assert.Single(dialogues, item => item.Id != original.Id);
        var route = $"{dialoguesPath}/{original.Id}";
        var otherRoute = $"{dialoguesPath}/{other.Id}";
        var preview = await editor.Read<PreviewStep>(HttpMethod.Post, $"{route}/previews");
        var otherPreview = await editor.Read<PreviewStep>(HttpMethod.Post, $"{otherRoute}/previews");
        var historyRoute = $"{route}/previews/{preview.PreviewId}/history";
        var otherHistoryRoute = $"{otherRoute}/previews/{otherPreview.PreviewId}/history";
        var history = await editor.Get<PreviewHistory>(historyRoute);
        var otherHistory = await editor.Get<PreviewHistory>(otherHistoryRoute);
        other = await editor.Get<DialogueSummary>(otherRoute);

        await guest.Expect(Unauthorized, HttpMethod.Delete, route);
        await editor.Expect(BadRequest, HttpMethod.Delete, route, includeToken: false);
        await editor.Expect(NoContent, HttpMethod.Put, route, new { title = " Архивная экспедиция " });
        var renamed = await editor.Get<DialogueSummary>(route);
        Assert.Equal((original.Id, "Архивная экспедиция", original.StartLineId, original.MinimumPlayerLevel),
            (renamed.Id, renamed.Title, renamed.StartLineId, renamed.MinimumPlayerLevel));
        Assert.NotEqual(original.Version, renamed.Version);
        Assert.Equal(history.Steps.ToArray(), (await editor.Get<PreviewHistory>(historyRoute)).Steps.ToArray());

        await editor.Expect(NoContent, HttpMethod.Delete, route);
        await editor.Expect(NotFound, HttpMethod.Delete, route);
        await editor.Expect(NotFound, HttpMethod.Get, route);
        await editor.Expect(NotFound, HttpMethod.Get, historyRoute);
        await editor.Expect(NotFound, HttpMethod.Put, route, new { title = "Архив" });
        Assert.Equal(other, Assert.Single(await editor.Get<DialogueSummary[]>(dialoguesPath)));
        Assert.Equal(otherHistory.Steps.ToArray(), (await editor.Get<PreviewHistory>(otherHistoryRoute)).Steps.ToArray());

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        Assert.False(await db.Lines.AnyAsync(line => line.DialogueId == original.Id));
        Assert.False(await db.Choices.AnyAsync(choice => choice.DialogueId == original.Id));
        Assert.False(await db.Previews.AnyAsync(item => item.Id == preview.PreviewId));
        Assert.False(await db.Set<PreviewHistoryEntry>().AnyAsync(step => step.PreviewId == preview.PreviewId));
        Assert.True(await db.Characters.AnyAsync(character => character.Id == preview.Line.SpeakerId));
    }

    private WebApplicationFactory<Program> Start(DialogueTestData data) =>
        data.Start(Environments.Development,
            new("DialogueSystem:AdminPassword", adminPassword), new("DialogueSystem:EditorPassword", editorPassword));
}
