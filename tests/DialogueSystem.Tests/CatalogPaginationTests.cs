using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class CatalogPaginationTests
{
    [Fact]
    public async Task CatalogPagesUseStableCursorsAndLiteralUnicodeSearch()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        db.Dialogues.AddRange(Enumerable.Range(1, 47).Select(id => new Dialogue { Title = $"Сценарий {id}" }));
        db.Characters.AddRange(Enumerable.Range(1, 125).Select(id => new Character { Name = $"Персонаж {id}" }));
        db.Characters.Add(new Character { Name = "Литерал %_'" });
        await db.SaveChangesAsync();
        using var client = new DialogueApiClient(app);
        using var guest = new DialogueApiClient(app);
        await guest.Expect(Unauthorized, HttpMethod.Get, "/api/dialogues/page");
        await client.Login("editor", password);
        var path = "/api/dialogues/page?q=" + Uri.EscapeDataString("СЦЕНАРИЙ");
        var page = await client.Get<CatalogPage<DialogueOverview>>(path);
        Assert.Equal(20, page.Items.Count);
        var ids = page.Items.Select(item => item.Id).ToList();
        var added = await client.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues", Created, new { title = "Сценарий новый" });
        while (page.NextBeforeId.HasValue)
        {
            page = await client.Get<CatalogPage<DialogueOverview>>(path + "&before=" + page.NextBeforeId);
            Assert.InRange(page.Items.Count, 1, 20);
            ids.AddRange(page.Items.Select(item => item.Id));
        }
        Assert.Equal(47, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.DoesNotContain(added.Id, ids);
        Assert.Equal(ids.OrderDescending(), ids);
        Assert.Empty((await client.Get<CatalogPage<DialogueOverview>>(path + "&hasStart=true")).Items);
        var literal = await client.Get<CatalogPage<CharacterSummary>>("/api/characters/page?q=" + Uri.EscapeDataString("%_'"));
        Assert.Single(literal.Items);
        Assert.Equal(20, (await client.Get<CatalogPage<CharacterSummary>>("/api/characters/page?q=" + Uri.EscapeDataString("ПЕРСОНАЖ"))).Items.Count);
        Assert.Equal(100, (await client.Get<CharacterSummary[]>("/api/characters")).Length);
        await client.Expect(BadRequest, HttpMethod.Get, "/api/characters/page?before=0");
    }

    [Fact]
    public async Task LinesAndCharacterUsagesAreBoundedAndKeepGlobalTotals()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var speaker = await db.Characters.SingleAsync(character => character.Name == "Марта");
        var dialogue = new Dialogue { Title = "Большой диалог" };
        dialogue.Lines.AddRange(Enumerable.Range(1, 45).Select(id => new DialogueLine
            { SpeakerId = speaker.Id, Text = $"Реплика {id} " + new string('я', 1000), IsEnding = true }));
        db.Dialogues.Add(dialogue);
        db.Dialogues.AddRange(Enumerable.Range(1, 24).Select(id => new Dialogue
        {
            Title = $"Ещё {id}", Lines = { new DialogueLine { SpeakerId = speaker.Id, Text = "Текст", IsEnding = true } }
        }));
        await db.SaveChangesAsync();
        using var client = new DialogueApiClient(app);
        await client.Login("editor", password);
        var page = await client.Get<CatalogPage<LineReference>>($"/api/dialogues/{dialogue.Id}/lines/page?q=" + Uri.EscapeDataString("РЕПЛИКА"));
        Assert.Equal(20, page.Items.Count);
        Assert.All(page.Items, item => Assert.True(item.Text.Length <= 241));
        var usage = await client.Get<CharacterUsage>($"/api/characters/{speaker.Id}/usage");
        Assert.Equal(74, usage.LineCount);
        Assert.Equal(26, usage.DialogueCount);
        Assert.Equal(20, usage.Dialogues.Count);
        var rest = await client.Get<CharacterUsage>($"/api/characters/{speaker.Id}/usage?before={usage.NextBeforeId}");
        Assert.Equal(6, rest.Dialogues.Count);
        Assert.Equal(usage.LineCount, rest.LineCount);
        Assert.Empty(usage.Dialogues.Select(item => item.DialogueId).Intersect(rest.Dialogues.Select(item => item.DialogueId)));
        await client.Expect(NotFound, HttpMethod.Get, "/api/dialogues/2147483647/lines/page");
    }
}
