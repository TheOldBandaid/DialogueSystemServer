using DialogueSystem.Application;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class ConcurrentEditingTests
{
    [Fact]
    public async Task RejectsStaleLineAndChoiceUpdatesWithoutLosingNewerText()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var a = new DialogueApiClient(app);
        using var b = new DialogueApiClient(app);
        await a.Login("editor", password);
        await b.Login("editor", password);
        var dialogue = (await a.GetDialogues("/api/dialogues"))[0];
        var path = $"/api/dialogues/{dialogue.Id}/lines/{dialogue.StartLineId}";
        var original = await a.Get<LineContext>(path + "/context");
        var line = original.Line;
        await a.Expect(NoContent, HttpMethod.Put, path,
            new { line.SpeakerId, text = "Правка первого редактора", line.IsEnding, line.IsStoryBranch, line.Version });
        await b.Expect(Conflict, HttpMethod.Put, path,
            new { line.SpeakerId, line.Text, line.IsEnding, isStoryBranch = !line.IsStoryBranch, line.Version });
        var fresh = (await b.Get<LineContext>(path + "/context")).Line;
        Assert.Equal("Правка первого редактора", fresh.Text);
        Assert.Equal(line.IsStoryBranch, fresh.IsStoryBranch);
        await b.Expect(NoContent, HttpMethod.Put, path,
            new { fresh.SpeakerId, fresh.Text, fresh.IsEnding, fresh.IsStoryBranch, fresh.Version });

        var choice = original.Outgoing[0];
        var choicePath = $"{path}/choices/{choice.Id}";
        await a.Expect(NoContent, HttpMethod.Put, choicePath,
            new { targetLineId = choice.Line.Id, text = "Новый ответ", choice.Position, choice.RequiredState, choice.NextState, choice.Version });
        await b.Expect(Conflict, HttpMethod.Put, choicePath,
            new { targetLineId = choice.Line.Id, choice.Text, choice.Position, choice.RequiredState, choice.NextState, choice.Version });
        Assert.Equal("Новый ответ", (await b.Get<LineContext>(path + "/context")).Outgoing[0].Text);

        using var missing = await a.Send(HttpMethod.Put, path,
            new { line.SpeakerId, text = "Без версии" });
        Assert.Equal((System.Net.HttpStatusCode)428, missing.StatusCode);
    }

    [Fact]
    public async Task AcceptsOnlyOneConcurrentWriteToTheSameVersion()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var a = new DialogueApiClient(app);
        using var b = new DialogueApiClient(app);
        await a.Login("editor", password);
        await b.Login("editor", password);
        var dialogue = (await a.GetDialogues("/api/dialogues"))[0];
        var path = $"/api/dialogues/{dialogue.Id}/lines/{dialogue.StartLineId}";
        var line = (await a.Get<LineContext>(path + "/context")).Line;
        var replies = await Task.WhenAll(
            a.Send(HttpMethod.Put, path, new { line.SpeakerId, text = "Первый", line.IsEnding, line.IsStoryBranch, line.Version }),
            b.Send(HttpMethod.Put, path, new { line.SpeakerId, text = "Второй", line.IsEnding, line.IsStoryBranch, line.Version }));
        try { Assert.Equal(new[] { NoContent, Conflict }, replies.Select(reply => reply.StatusCode).Order()); }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }
}
