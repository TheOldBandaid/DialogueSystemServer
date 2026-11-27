using System.Text.Json;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class LargeEditorTests
{
    [Fact]
    public async Task ContextPagesBoundLinksAndRefuseToMixChangedGraphs()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var speaker = await db.Characters.FirstAsync();
        var dialogue = new Dialogue { Title = "Много связей" };
        var root = new DialogueLine { SpeakerId = speaker.Id, Text = "Центр" };
        dialogue.Lines.Add(root);
        dialogue.Lines.AddRange(Enumerable.Range(1, 45).Select(id => new DialogueLine
            { SpeakerId = speaker.Id, Text = $"Реплика {id}", IsEnding = true }));
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync();
        var position = 0;
        foreach (var other in dialogue.Lines.Skip(1))
        {
            db.Choices.Add(new DialogueChoice { DialogueId = dialogue.Id, SourceLineId = root.Id,
                TargetLineId = other.Id, Text = "Ответ", Position = ++position });
            db.Choices.Add(new DialogueChoice { DialogueId = dialogue.Id, SourceLineId = other.Id,
                TargetLineId = root.Id, Text = "Вход", Position = 1 });
        }
        await db.SaveChangesAsync();
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var path = $"/api/dialogues/{dialogue.Id}/lines/{root.Id}/context";
        var page = await client.Get<LineContext>(path);
        Assert.Equal(20, page.Outgoing.Count);
        Assert.Equal(20, page.Incoming.Count);
        Assert.Equal(45, page.OutgoingCount);
        Assert.Equal(46, page.SuggestedChoicePosition);
        var ids = page.Outgoing.Select(choice => choice.Id).ToList();
        var version = page.Dialogue!.Version;
        while (page.NextOutgoingPosition.HasValue)
        {
            page = await client.Get<LineContext>($"{path}?outgoingAfter={page.NextOutgoingPosition}&version={version}");
            ids.AddRange(page.Outgoing.Select(choice => choice.Id));
        }
        Assert.Equal(45, ids.Distinct().Count());
        var last = await client.Get<ChoiceSummary>($"/api/dialogues/{dialogue.Id}/lines/{root.Id}/choices/{ids[^1]}");
        Assert.Equal(45, last.Position);
        await client.Expect(BadRequest, HttpMethod.Get, path + "?incomingAfterLine=1");
        await client.Expect((System.Net.HttpStatusCode)428, HttpMethod.Get, path + "?outgoingAfter=20");
        var lastId = ids[^1];
        await db.Choices.Where(choice => choice.Id == lastId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(choice => choice.Text, "Правка"));
        await client.Expect(Conflict, HttpMethod.Get, $"{path}?outgoingAfter=20&version={version}");
    }

    [Fact]
    public async Task MapHasCompleteTopologyButDoesNotTransferFullDialogueText()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var speaker = await db.Characters.FirstAsync();
        var dialogue = new Dialogue { Title = "Карта" };
        dialogue.Lines.AddRange(Enumerable.Range(1, 100).Select(id => new DialogueLine
            { SpeakerId = speaker.Id, Text = new string('я', 4000), IsEnding = id == 100 }));
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync();
        for (var index = 0; index < 99; index++)
            db.Choices.Add(new DialogueChoice { DialogueId = dialogue.Id, SourceLineId = dialogue.Lines[index].Id,
                TargetLineId = dialogue.Lines[index + 1].Id, Text = new string('о', 500), Position = 1 });
        await db.SaveChangesAsync();
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var map = await client.Get<DialogueMapSnapshot>($"/api/dialogues/{dialogue.Id}/map");
        var full = await client.Get<DialogueDocument>($"/api/dialogues/{dialogue.Id}/export");
        Assert.Equal(100, map.Lines.Count);
        Assert.Equal(99, map.Choices.Count);
        Assert.All(map.Lines, line => Assert.True(line.Text.Length <= 241));
        Assert.All(map.Choices, choice => Assert.True(choice.Text.Length <= 121));
        Assert.True(JsonSerializer.Serialize(map).Length < JsonSerializer.Serialize(full).Length / 5);
        Assert.Equal(4000, (await client.Get<LineSummary>($"/api/dialogues/{dialogue.Id}/lines/{dialogue.Lines[0].Id}")).Text.Length);
    }
}
