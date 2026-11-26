using DialogueSystem.Application;
using DialogueSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class PreviewPaginationTests
{
    [Fact]
    public async Task PagesEqualTimestampsWithoutRepeatsAndRejectsAnotherUsersCursor()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings:
            [new("DialogueSystem:EditorPassword", password), new("DialogueSystem:AdminPassword", password)]);
        using var editor = new DialogueApiClient(app);
        using var admin = new DialogueApiClient(app);
        await editor.Login("editor", password);
        await admin.Login("admin", password);
        var dialogue = (await editor.Get<DialogueSummary[]>("/api/dialogues"))[0];
        var route = $"/api/dialogues/{dialogue.Id}/previews";
        var expected = new HashSet<Guid>();
        for (var i = 0; i < 25; i++) expected.Add((await editor.Read<PreviewStep>(HttpMethod.Post, route)).PreviewId);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            await db.Previews.ExecuteUpdateAsync(setters => setters.SetProperty(preview => preview.StartedAtUnixMilliseconds, 123L));
        }
        var first = await editor.Get<PreviewSessionPage>(route + "/page");
        Assert.Equal(20, first.Items.Count);
        Assert.NotNull(first.NextBeforeId);
        var newer = await editor.Read<PreviewStep>(HttpMethod.Post, route);
        var second = await editor.Get<PreviewSessionPage>($"{route}/page?before={first.NextBeforeId}");
        Assert.Equal(5, second.Items.Count);
        Assert.Null(second.NextBeforeId);
        Assert.True(expected.SetEquals(first.Items.Concat(second.Items).Select(item => item.PreviewId)));
        Assert.DoesNotContain(second.Items, item => item.PreviewId == newer.PreviewId);
        var other = await admin.Read<PreviewStep>(HttpMethod.Post, route);
        await editor.Expect(NotFound, HttpMethod.Get, $"{route}/page?before={other.PreviewId}");
    }

    [Fact]
    public async Task ReadsALongLoopHistoryInBoundedOrderedPages()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var document = new DialogueDocument(1, "Длинная история", 1,
            [new(1, "Персонаж", "Повторяемый вопрос", false, false), new(2, "Персонаж", "Конец", true, false)],
            [new(1, 1, "Повторить", 1), new(1, 2, "Завершить", 2)]);
        var dialogue = await editor.Read<DialogueSummary>(HttpMethod.Post, "/api/dialogues/import", Created, document);
        var route = $"/api/dialogues/{dialogue.Id}/previews";
        var current = await editor.Read<PreviewStep>(HttpMethod.Post, route);
        var choice = current.Choices[0].Id;
        for (var i = 0; i < 205; i++)
            current = await editor.Read<PreviewStep>(HttpMethod.Post, $"{route}/{current.PreviewId}/choices",
                payload: new SelectChoice(choice, current.Step));
        var steps = new List<int>();
        var after = -1;
        do
        {
            var page = await editor.Get<PreviewHistory>($"{route}/{current.PreviewId}/history?after={after}");
            Assert.InRange(page.Steps.Count, 1, 100);
            Assert.Equal(205, page.CurrentStep);
            steps.AddRange(page.Steps.Select(step => step.Step));
            after = page.NextAfterStep ?? -1;
        } while (after >= 0);
        Assert.Equal(Enumerable.Range(0, 206), steps);
        await editor.Expect(BadRequest, HttpMethod.Get, $"{route}/{current.PreviewId}/history?after=-2");
    }
}
