using DialogueSystem.Application;
using DialogueSystem.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueRevisionTests
{
    private const string Dialogues = "/api/dialogues";
    private readonly string password = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task PreservesSnapshotAndRestoresAnIndependentCopyAfterRestart()
    {
        using var data = new DialogueTestData();
        DialogueDocument snapshot;
        DialogueRevisionSummary revision;
        string source;
        string historyPath;
        PreviewHistory oldHistory;

        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]))
        using (var editor = new DialogueApiClient(app))
        using (var guest = new DialogueApiClient(app))
        {
            await editor.Login("editor", password);
            var dialogues = await editor.GetDialogues(Dialogues);
            var sample = Assert.Single(dialogues, item => item.Title == "Кино после патруля");
            var other = Assert.Single(dialogues, item => item.Id != sample.Id);
            source = $"{Dialogues}/{sample.Id}";
            var revisions = source + "/revisions";
            var input = new { label = "  До правок  " };
            await guest.Expect(Unauthorized, HttpMethod.Get, revisions);
            await guest.Expect(Unauthorized, HttpMethod.Post, revisions, input);
            await editor.Expect(BadRequest, HttpMethod.Post, revisions, input, includeToken: false);
            await editor.Expect(BadRequest, HttpMethod.Post, revisions,
            new { label = " " });
            Assert.Empty((await editor.Get<DialogueRevisionPage>(revisions)).Items);
            await editor.Expect(NoContent, HttpMethod.Put, source + "",
            DialogueSettings.From(await editor.Get<DialogueSummary>(source + "")) with { MinimumPlayerLevel = 4 });
            snapshot = await editor.Get<DialogueDocument>(source + "/export");
            var preview = await editor.Read<PreviewStep>(
                HttpMethod.Post, source + "/previews", payload: new { playerLevel = 4 });
            historyPath = $"{source}/previews/{preview.PreviewId}/history";
            oldHistory = await editor.Get<PreviewHistory>(historyPath);

            revision = await editor.Read<DialogueRevisionSummary>(HttpMethod.Post, revisions, Created, input);
            Assert.Equal("До правок", revision.Label);
            Assert.Equal("editor", revision.Author);
            Assert.Equal(snapshot.Lines.Count, revision.LineCount);
            Assert.Equal(snapshot.Choices.Count, revision.ChoiceCount);
            var otherRevision = $"{Dialogues}/{other.Id}/revisions/{revision.Id}";
            await editor.Expect(NotFound, HttpMethod.Get, otherRevision + "/export");
            await editor.Expect(NotFound, HttpMethod.Post, otherRevision + "/copy");

            await editor.Expect(NoContent, HttpMethod.Put, source,
            DialogueSettings.From(await editor.Get<DialogueSummary>(source)) with { Title = "Текущая редакция" });
            await editor.Expect(NoContent, HttpMethod.Put, source + "",
            DialogueSettings.From(await editor.Get<DialogueSummary>(source + "")) with { MinimumPlayerLevel = 2 });
            await editor.Expect(NoContent, HttpMethod.Put, $"{source}/lines/{preview.Line.Id}",
            new { speakerId = preview.Line.SpeakerId, text = "Обновлённая стартовая реплика", isStoryBranch = true, version = (await editor.Get<LineSummary>($"{source}/lines/{preview.Line.Id}")).Version });
        }

        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]))
        using (var editor = new DialogueApiClient(app))
        {
            await editor.Login("editor", password);
            var revisions = source + "/revisions";
            Assert.Equal(revision, Assert.Single((await editor.Get<DialogueRevisionPage>(revisions)).Items));
            var revisionPath = $"{revisions}/{revision.Id}";
            var saved = await editor.Get<DialogueDocument>(revisionPath + "/export");
            Assert.Equal(snapshot.Title, saved.Title);
            Assert.Equal(snapshot.MinimumPlayerLevel, saved.MinimumPlayerLevel);
            Assert.Equal(snapshot.StartLineId, saved.StartLineId);
            Assert.Equal(snapshot.Lines.ToArray(), saved.Lines.ToArray());
            Assert.Equal(snapshot.Choices.ToArray(), saved.Choices.ToArray());

            await editor.Expect(BadRequest, HttpMethod.Post, revisionPath + "/copy", includeToken: false);
            var copy = await editor.Read<DialogueSummary>(HttpMethod.Post, revisionPath + "/copy", Created);
            Assert.Equal("Кино после патруля (версия «До правок»)", copy.Title);
            var restored = await editor.Get<DialogueDocument>($"{Dialogues}/{copy.Id}/export");
            var ids = saved.Lines.Zip(restored.Lines).ToDictionary(pair => pair.First.Id, pair => pair.Second.Id);
            Assert.Empty(ids.Keys.Intersect(ids.Values));
            Assert.Equal(saved.Lines.Select(line => line with { Id = ids[line.Id] }), restored.Lines);
            Assert.Equal(ids[saved.StartLineId!.Value], restored.StartLineId);
            Assert.Equal(saved.MinimumPlayerLevel, restored.MinimumPlayerLevel);
            Assert.Equal(saved.Choices.Select(choice => choice with
            {
                SourceLineId = ids[choice.SourceLineId],
                TargetLineId = ids[choice.TargetLineId]
            }), restored.Choices);

            var current = await editor.Get<DialogueDocument>(source + "/export");
            Assert.Equal("Текущая редакция", current.Title);
            Assert.Equal(2, current.MinimumPlayerLevel);
            Assert.Contains(current.Lines, line => line.Text == "Обновлённая стартовая реплика");
            Assert.Equal(oldHistory.Steps.ToArray(), (await editor.Get<PreviewHistory>(historyPath)).Steps.ToArray());

            await editor.Expect(NoContent, HttpMethod.Delete, source,
            ifMatch: (await editor.Get<DialogueSummary>(source)).DeleteVersion);
            await editor.Expect(NotFound, HttpMethod.Get, revisions);
            Assert.Equal(copy, await editor.Get<DialogueSummary>($"{Dialogues}/{copy.Id}"));
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            Assert.False(await db.DialogueRevisions.AnyAsync(item => item.Id == revision.Id));
        }
    }

    [Fact]
    public async Task PagesVersionsFromNewestToOldestWithoutRepeatingEntries()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var draft = await editor.Read<DialogueSummary>(HttpMethod.Post, Dialogues, Created, new { title = "Черновик" });
        var route = $"{Dialogues}/{draft.Id}/revisions";
        var saved = new List<int>();
        for (var index = 0; index < 21; index++)
        {
            var revision = await editor.Read<DialogueRevisionSummary>(
                HttpMethod.Post, route, Created, new { label = $"Версия {index + 1}" });
            saved.Add(revision.Id);
        }

        var first = await editor.Get<DialogueRevisionPage>(route);
        Assert.Equal(20, first.Items.Count);
        Assert.Equal(first.Items[^1].Id, first.NextBeforeId);
        var second = await editor.Get<DialogueRevisionPage>($"{route}?before={first.NextBeforeId}");
        Assert.Single(second.Items);
        Assert.Null(second.NextBeforeId);
        Assert.Equal(saved.AsEnumerable().Reverse(), first.Items.Concat(second.Items).Select(item => item.Id));
    }
}
