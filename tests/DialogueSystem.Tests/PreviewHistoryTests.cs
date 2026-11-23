using DialogueSystem.Infrastructure.Data;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class PreviewHistoryTests
{
    private const string Dialogues = "/api/dialogues";
    private readonly string editorPassword = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task KeepsTextAndChoiceSnapshotsAfterEditingAndRestart()
    {
        using var data = new DialogueTestData();
        var adminPassword = Guid.NewGuid().ToString("N");
        string historyPath;
        string previews;
        PreviewHistory savedHistory;

        await using (var app = data.Start(settings:
            [new("DialogueSystem:EditorPassword", editorPassword), new("DialogueSystem:AdminPassword", adminPassword)]))
        using (var editor = new DialogueApiClient(app))
        using (var admin = new DialogueApiClient(app))
        using (var guest = new DialogueApiClient(app))
        {
            await editor.Login("editor", editorPassword);
            await admin.Login("admin", adminPassword);
            var dialogue = Assert.Single(await editor.Get<DialogueSummary[]>(Dialogues),
                item => item.Title == "Ключ от архива");
            var path = $"{Dialogues}/{dialogue.Id}";
            previews = $"{path}/previews";
            Assert.Empty(await editor.Get<PreviewSessionSummary[]>(previews));

            var start = await editor.Read<PreviewStep>(HttpMethod.Post, previews);
            historyPath = $"{previews}/{start.PreviewId}/history";
            var opening = await editor.Get<PreviewHistory>(historyPath);
            var first = Assert.Single(opening.Steps);
            Assert.Equal(0, opening.CurrentStep);
            Assert.Equal(start.Line.Text, first.Text);
            Assert.Null(first.ChoiceText);

            var selected = start.Choices[0];
            var next = await editor.Read<PreviewStep>(HttpMethod.Post, $"{previews}/{start.PreviewId}/choices",
                payload: new SelectChoice(selected.Id, start.Step));
            savedHistory = await editor.Get<PreviewHistory>(historyPath);
            Assert.Equal(new[] { 0, 1 }, savedHistory.Steps.Select(step => step.Step));
            Assert.Equal(selected.Text, savedHistory.Steps[1].ChoiceText);
            Assert.Equal(next.Line.Speaker, savedHistory.Steps[1].Speaker);
            Assert.Equal(next.Line.Text, savedHistory.Steps[1].Text);

            await guest.Expect(Unauthorized, HttpMethod.Get, historyPath);
            Assert.Empty(await admin.Get<PreviewSessionSummary[]>(previews));
            await admin.Expect(NotFound, HttpMethod.Get, historyPath);
            await editor.Expect(NotFound, HttpMethod.Get, $"{Dialogues}/0/previews/{start.PreviewId}/history");

            var newChoice = new { text = "Новая формулировка", targetLineId = next.Line.Id, position = 1 };
            await editor.Expect(NoContent, HttpMethod.Put, $"{path}/lines/{start.Line.Id}/choices/{selected.Id}", newChoice);
            await editor.Expect(NoContent, HttpMethod.Put, $"{path}/lines/{next.Line.Id}",
                new { next.Line.SpeakerId, text = "Новый текст реплики" });
        }

        await using (var app = data.Start(settings: [new("DialogueSystem:EditorPassword", editorPassword)]))
        using (var editor = new DialogueApiClient(app))
        {
            await editor.Login("editor", editorPassword);
            var session = Assert.Single(await editor.Get<PreviewSessionSummary[]>(previews));
            Assert.Equal(savedHistory.PreviewId, session.PreviewId);
            Assert.Equal(1, session.Step);
            Assert.NotNull(session.StartedAtUnixMilliseconds);
            var restored = await editor.Get<PreviewHistory>(historyPath);
            Assert.Equal(savedHistory.Steps.ToArray(), restored.Steps.ToArray());
        }
    }

    [Fact]
    public async Task MigratesExistingSessionAndRecordsOnlyNewSteps()
    {
        using var data = new DialogueTestData();
        var connection = new SqliteConnectionStringBuilder { DataSource = data.DatabasePath, ForeignKeys = true };
        var options = new DbContextOptionsBuilder<DialogueDbContext>().UseSqlite(connection.ToString()).Options;
        var previewId = Guid.NewGuid();
        var dialogue = new Dialogue { Title = "Старое прохождение" };
        var speaker = new Character { Name = "Стражник" };
        var start = new DialogueLine { Speaker = speaker, Text = "Пора идти." };
        var end = new DialogueLine { Speaker = speaker, Text = "До встречи.", IsEnding = true };
        var account = new Account { Id = 1, Login = "editor", Role = "Editor", PasswordHash = "" };
        account.PasswordHash = new PasswordHasher<Account>().HashPassword(account, editorPassword);
        const string farewell = "Попрощаться";

        await using (var db = new DialogueDbContext(options))
        {
            await db.Database.MigrateAsync("20261122123604_AddMinimumPlayerLevel");
            dialogue.Lines.AddRange([start, end]);
            db.Dialogues.Add(dialogue);
            await db.SaveChangesAsync();
            dialogue.StartLineId = start.Id;
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Choices (DialogueId, SourceLineId, TargetLineId, Text, Position) VALUES ({dialogue.Id}, {start.Id}, {end.Id}, {farewell}, {1})");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Accounts (Id, Login, Role, PasswordHash) VALUES ({account.Id}, {account.Login}, {account.Role}, {account.PasswordHash})");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Previews (Id, UserId, DialogueId, CurrentLineId, Step) VALUES ({previewId}, {account.Id}, {dialogue.Id}, {start.Id}, {0})");
        }

        await using var app = data.Start();
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", editorPassword);
        var previews = $"{Dialogues}/{dialogue.Id}/previews";
        var session = Assert.Single(await editor.Get<PreviewSessionSummary[]>(previews));
        Assert.Null(session.StartedAtUnixMilliseconds);
        Assert.Empty((await editor.Get<PreviewHistory>($"{previews}/{previewId}/history")).Steps);

        var context = await editor.Get<LineContext>($"{Dialogues}/{dialogue.Id}/lines/{start.Id}/context");
        var ended = await editor.Read<PreviewStep>(HttpMethod.Post, $"{previews}/{previewId}/choices",
            payload: new SelectChoice(Assert.Single(context.Outgoing).Id, 0));
        Assert.True(ended.Line.IsEnding);
        var history = await editor.Get<PreviewHistory>($"{previews}/{previewId}/history");
        Assert.Equal(1, history.CurrentStep);
        Assert.Equal(new PreviewHistoryStep(1, end.Id, speaker.Name, end.Text, true, farewell), Assert.Single(history.Steps));
    }
}
