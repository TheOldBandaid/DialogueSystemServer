using System.Text.Json;
using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using static DialogueSystem.Web.Controllers.AccountsController;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueApiTests
{
    private readonly string editorPassword = Guid.NewGuid().ToString("N");
    private readonly string adminPassword = Guid.NewGuid().ToString("N");
    private const string DemoTitle = "Ключ от архива";

    [Fact]
    public async Task EnforcesRolesAndCsrfProtection()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var guest = new DialogueApiClient(app);
        using var editor = new DialogueApiClient(app);
        using var admin = new DialogueApiClient(app);

        await guest.Expect(Unauthorized, HttpMethod.Get, "/api/dialogues");
        await editor.Login("editor", editorPassword);
        await admin.Login("admin", adminPassword);

        const string accounts = "/api/accounts";
        var users = await admin.Get<AccountSummary[]>(accounts);
        var editorId = Assert.Single(users, user => user.Login == "editor").Id;
        var adminId = Assert.Single(users, user => user.Login == "admin").Id;
        var editorRole = $"{accounts}/{editorId}/role";
        var makeAdmin = new { role = "Administrator" };
        var makeEditor = new { role = "Editor" };

        await editor.Expect(Forbidden, HttpMethod.Put, editorRole, makeAdmin);
        await admin.Expect(Conflict, HttpMethod.Put, $"{accounts}/{adminId}/role", makeEditor);
        await admin.Expect(BadRequest, HttpMethod.Put, editorRole, makeAdmin, includeToken: false);
        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeAdmin);
        Assert.Equal("Administrator", (await editor.Get<AccountSummary>($"{accounts}/me")).Role);

        await admin.Expect(NoContent, HttpMethod.Put, editorRole, makeEditor);
        await editor.Expect(Forbidden, HttpMethod.Get, accounts);
    }

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
        var demo = Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"));
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
        var speakerId = Assert.Single(await editor.Get<Character[]>("/api/characters")).Id;
        var draft = await CreateDialogue("Архивный черновик");
        var other = await CreateDialogue("Другой диалог");
        await editor.Expect(UnprocessableEntity, HttpMethod.Post, $"{draft}/previews");

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
        Assert.Contains(invalid.Errors, error => error.Contains($"Из реплики {loop}", StringComparison.Ordinal));
        await AddChoice(loop, ending, 2);
        await editor.Expect(OK, HttpMethod.Post, $"{draft}/previews");

        await editor.Expect(Conflict, HttpMethod.Delete, $"{draft}/lines/{ending}");
        await editor.Expect(Conflict, HttpMethod.Delete, $"{draft}/lines/{start}");
        var orphan = await AddLine(draft, "Неиспользуемая реплика");
        await AddChoice(orphan, orphan, 1);
        var validation = await editor.Get<DialogueValidation>($"{draft}/validation");
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
    public async Task KeepsEditedTextAndRouteAfterRestart()
    {
        using var data = new DialogueTestData();
        const string edited = "Изменённое начало";
        string startContext;
        string branchContext;

        await using (var app = Start(data))
        using (var editor = new DialogueApiClient(app))
        {
            await editor.Login("editor", editorPassword);
            var demo = Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"), item => item.Title == DemoTitle);
            var path = $"/api/dialogues/{demo.Id}";
            var branch = Assert.Single(await editor.Get<LineSummary[]>($"{path}/story-branches"));
            branchContext = $"{path}/lines/{branch.Id}/context";
            var startPath = $"{path}/lines/{demo.StartLineId}";
            startContext = $"{startPath}/context";
            var start = await editor.Get<LineContext>(startContext);

            var direct = Assert.Single(start.Outgoing, choice => choice.Line.Id == branch.Id);
            var detour = Assert.Single(start.Outgoing, choice => choice.Line.Id != branch.Id);
            await editor.Expect(NoContent, HttpMethod.Put, $"{startPath}/choices/{direct.Id}",
                new { direct.Text, direct.Position, targetLineId = detour.Line.Id });
            await editor.Expect(NoContent, HttpMethod.Put, startPath, new { start.Line.SpeakerId, text = edited });
            Assert.Single((await editor.Get<LineContext>(branchContext)).Incoming);
        }

        await using (var app = Start(data))
        using (var editor = new DialogueApiClient(app))
        {
            await editor.Login("editor", editorPassword);
            Assert.Single((await editor.Get<LineContext>(branchContext)).Incoming);
            Assert.Equal(edited, (await editor.Get<LineContext>(startContext)).Line.Text);
            Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"), item => item.Title == DemoTitle);
        }
    }

    [Fact]
    public async Task ShowsBothDirectionsOfSelfLoopAndOrdersContextChoices()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);

        var demo = Assert.Single(await editor.Get<DialogueSummary[]>("/api/dialogues"));
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

    private WebApplicationFactory<DialogueDbContext> Start(DialogueTestData data) =>
        data.Start(Environments.Development,
            new("DialogueSystem:AdminPassword", adminPassword), new("DialogueSystem:EditorPassword", editorPassword));
}
