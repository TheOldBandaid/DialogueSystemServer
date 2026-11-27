using System.Text.Json;
using DialogueSystem.Infrastructure.Data;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueLevelTests
{
    private const string Dialogues = "/api/dialogues";
    private const string DemoTitle = "Ключ от архива";
    private readonly string password = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task EnforcesLevelAtStartAndKeepsExistingPreviewAfterChangingThreshold()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var demo = Assert.Single(await editor.GetDialogues(Dialogues), item => item.Title == DemoTitle);
        var route = $"{Dialogues}/{demo.Id}";
        var previews = $"{route}/previews";
        var setting = $"{route}";

        Assert.Null(demo.MinimumPlayerLevel);
        await editor.Expect(NoContent, HttpMethod.Put, setting,
            DialogueSettings.From(await editor.Get<DialogueSummary>(setting)) with { MinimumPlayerLevel = 5 });
        Assert.Equal(5, (await editor.Get<DialogueSummary>(route)).MinimumPlayerLevel);
        foreach (var input in new PreviewInput?[] { null, new(4) })
        {
            var rejected = await editor.Read<JsonElement>(HttpMethod.Post, previews, UnprocessableEntity, input);
            Assert.Equal(5, rejected.GetProperty("minimumPlayerLevel").GetInt32());
        }

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            Assert.False(await db.Previews.AnyAsync());
        }

        var started = await editor.Read<PreviewStep>(HttpMethod.Post, previews, payload: new PreviewInput(5));
        await editor.Expect(NoContent, HttpMethod.Put, setting,
            DialogueSettings.From(await editor.Get<DialogueSummary>(setting)) with { MinimumPlayerLevel = 6 });
        var advance = await editor.Read<PreviewStep>(HttpMethod.Post, $"{previews}/{started.PreviewId}/choices",
            payload: new SelectChoice(started.Choices[0].Id, started.Step));
        Assert.Equal(1, advance.Step);

        await editor.Expect(NoContent, HttpMethod.Put, setting,
            DialogueSettings.From(await editor.Get<DialogueSummary>(setting)) with { MinimumPlayerLevel = (int?)null });
        Assert.Null((await editor.Get<DialogueSummary>(route)).MinimumPlayerLevel);
        await editor.Expect(OK, HttpMethod.Post, previews);
    }

    [Fact]
    public async Task KeepsThresholdDuringRenamingExportAndImport()
    {
        using var data = new DialogueTestData();
        await using var app = Start(data);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var original = Assert.Single(await editor.GetDialogues(Dialogues), item => item.Title == DemoTitle);
        var route = $"{Dialogues}/{original.Id}";

        await editor.Expect(NoContent, HttpMethod.Put, $"{route}",
            DialogueSettings.From(await editor.Get<DialogueSummary>($"{route}")) with { MinimumPlayerLevel = 3 });
        await editor.Expect(NoContent, HttpMethod.Put, route,
            DialogueSettings.From(await editor.Get<DialogueSummary>(route)) with { Title = "Архив второго уровня" });
        var exported = await editor.Get<DialogueDocument>($"{route}/export");
        Assert.Equal(3, exported.MinimumPlayerLevel);

        var restored = await editor.Read<DialogueSummary>(HttpMethod.Post, $"{Dialogues}/import", Created, exported);
        Assert.Equal(3, restored.MinimumPlayerLevel);
        var preview = $"{Dialogues}/{restored.Id}/previews";
        await editor.Expect(UnprocessableEntity, HttpMethod.Post, preview,
            new PreviewInput(2));
        await editor.Expect(OK, HttpMethod.Post, preview,
            new PreviewInput(3));
    }

    private WebApplicationFactory<Program> Start(DialogueTestData data) =>
        data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
}
