using DialogueSystem.Application;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class ChoiceOrderingTests
{
    [Fact]
    public async Task SwapsOccupiedPositionsAndRejectsAnOutdatedOrder()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var dialogue = (await editor.Get<DialogueSummary[]>("/api/dialogues"))[0];
        var path = $"/api/dialogues/{dialogue.Id}/lines/{dialogue.StartLineId}";
        var before = await editor.Get<LineContext>(path + "/context");
        var first = before.Outgoing[0];
        var second = before.Outgoing[1];
        var move = $"{path}/choices/{first.Id}/move";
        var input = new { direction = 1, first.Version };
        await guest.Expect(Unauthorized, HttpMethod.Post, move, input);
        await editor.Expect(BadRequest, HttpMethod.Post, move, input, includeToken: false);
        await editor.Expect(NoContent, HttpMethod.Post, move, input);
        var after = await editor.Get<LineContext>(path + "/context");
        Assert.Equal(new[] { second.Id, first.Id }, after.Outgoing.Take(2).Select(choice => choice.Id));
        Assert.Equal(new[] { first.Position, second.Position }, after.Outgoing.Take(2).Select(choice => choice.Position));
        await editor.Expect(Conflict, HttpMethod.Post, move, input);
        var moved = after.Outgoing.Single(choice => choice.Id == first.Id);
        await editor.Expect(BadRequest, HttpMethod.Post, move, new { direction = 0, moved.Version });
        await editor.Expect(NoContent, HttpMethod.Post, move, new { direction = -1, moved.Version });
        var restored = await editor.Get<LineContext>(path + "/context");
        Assert.Equal(before.Outgoing.Select(choice => choice.Id), restored.Outgoing.Select(choice => choice.Id));
        await editor.Expect(NoContent, HttpMethod.Post, move, new { direction = -1, first.Version });
    }
}
