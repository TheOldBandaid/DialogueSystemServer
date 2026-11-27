using System.Text.Json.Nodes;
using DialogueSystem.Application;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueSettingsTests
{
    [Fact]
    public async Task SavesSettingsAtomicallyAndRejectsForeignStartsOrStaleVersions()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var dialogues = await client.GetDialogues("/api/dialogues");
        var original = dialogues[0];
        var other = dialogues[1];
        var path = $"/api/dialogues/{original.Id}";
        var invalid = new { title = "Изменения должны отмениться", startLineId = other.StartLineId,
            minimumPlayerLevel = 7, original.Version };
        using (var rejected = await client.Send(HttpMethod.Put, path, invalid, includeVersion: false))
            Assert.Equal(BadRequest, rejected.StatusCode);
        Assert.Equal(original, await client.Get<DialogueSummary>(path));

        var input = new { title = " Новые настройки ", startLineId = original.StartLineId,
            minimumPlayerLevel = 7, original.Version };
        using (var saved = await client.Send(HttpMethod.Put, path, input, includeVersion: false))
            Assert.Equal(NoContent, saved.StatusCode);
        var fresh = await client.Get<DialogueSummary>(path);
        Assert.Equal(("Новые настройки", original.StartLineId, 7), (fresh.Title, fresh.StartLineId, fresh.MinimumPlayerLevel));
        Assert.NotEqual(original.Version, fresh.Version);
        using (var stale = await client.Send(HttpMethod.Put, path,
                   new { title = "Старые данные", startLineId = (int?)null, minimumPlayerLevel = (int?)null, original.Version },
                   includeVersion: false))
            Assert.Equal(Conflict, stale.StatusCode);
        Assert.Equal(fresh, await client.Get<DialogueSummary>(path));
        using (var cleared = await client.Send(HttpMethod.Put, path,
                   new { fresh.Title, startLineId = (int?)null, minimumPlayerLevel = (int?)null, fresh.Version }, includeVersion: false))
            Assert.Equal(NoContent, cleared.StatusCode);
        var final = await client.Get<DialogueSummary>(path);
        Assert.Null(final.StartLineId);
        Assert.Null(final.MinimumPlayerLevel);
        Assert.Equal(fresh.Title, final.Title);
        await client.Expect(NotFound, HttpMethod.Put, path + "/start", new { lineId = original.StartLineId });
        await client.Expect(NotFound, HttpMethod.Put, path + "/minimum-level", new { minimumPlayerLevel = 3 });
    }

    [Theory]
    [InlineData("version", 428)]
    public async Task RequiresCompleteSettingsAndAnExplicitVersion(string omitted, int expected)
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var original = (await client.GetDialogues("/api/dialogues"))[0];
        var path = $"/api/dialogues/{original.Id}";
        var input = new JsonObject { ["title"] = original.Title, ["startLineId"] = original.StartLineId,
            ["minimumPlayerLevel"] = original.MinimumPlayerLevel, ["version"] = original.Version };
        input.Remove(omitted);
        using var response = await client.Send(HttpMethod.Put, path, input, includeVersion: false);
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(original, await client.Get<DialogueSummary>(path));
    }
}
