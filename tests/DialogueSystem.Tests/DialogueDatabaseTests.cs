using DialogueSystem.Web.Data;
using DialogueSystem.Web.Models;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DialogueSystem.Tests;

public sealed class DialogueDatabaseTests
{
    private const string DemoTitle = "Ключ от архива";

    [Fact]
    public async Task CreatesDatabaseSeedsDemoAndKeepsEditsAfterRestart()
    {
        using var data = new DialogueTestData();
        const string editedText = "Архив откроется утром.";
        int dialogueId;
        int startId;

        await using (var app = data.Start())
        using (var scope = app.Services.CreateScope())
        {
            Assert.True(File.Exists(data.DatabasePath));
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());

            var dialogue = await db.Dialogues.Include(item => item.Lines).ThenInclude(line => line.Choices)
                .SingleAsync(item => item.Title == DemoTitle);
            var start = Assert.Single(dialogue.Lines, line => line.Id == dialogue.StartLineId);
            var branch = Assert.Single(dialogue.Lines, line => line.IsStoryBranch);
            var endings = dialogue.Lines.Where(line => line.IsEnding).Select(line => line.Id).Order().ToArray();

            Assert.Equal(5, dialogue.Lines.Count);
            Assert.Equal(2, start.Choices.Count);
            Assert.Equal(2, endings.Length);
            Assert.Equal(endings, branch.Choices.Select(choice => choice.TargetLineId).Order());

            dialogueId = dialogue.Id;
            startId = start.Id;
            start.Text = editedText;
            await db.SaveChangesAsync();
        }

        await using (var app = data.Start())
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            var dialogue = await db.Dialogues.Include(item => item.Lines).SingleAsync(item => item.Title == DemoTitle);
            Assert.Equal(dialogueId, dialogue.Id);
            Assert.Equal(startId, dialogue.StartLineId);
            Assert.Equal(editedText, dialogue.Lines.Single(line => line.Id == startId).Text);
        }
    }

    [Theory]
    [InlineData(nameof(Environments.Production), "true")]
    [InlineData(nameof(Environments.Development), "false")]
    public async Task CreatesSchemaWithoutDemoWhenSeedingIsDisabled(string environment, string seedDemo)
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(environment, new KeyValuePair<string, string?>("DialogueSystem:SeedDemo", seedDemo));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();

        Assert.True(File.Exists(data.DatabasePath));
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await db.Dialogues.ToListAsync());
    }

    [Fact]
    public async Task RejectsChoiceInAnotherDialogueAndRepeatedPosition()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
        var firstChoice = await db.Choices.OrderBy(choice => choice.Id).FirstAsync();
        var speaker = await db.Characters.SingleAsync();
        var otherDialogue = new Dialogue { Title = "Другой диалог" };
        var otherLine = new DialogueLine { Speaker = speaker, Text = "Чужая реплика" };
        otherDialogue.Lines.Add(otherLine);
        db.Dialogues.Add(otherDialogue);
        await db.SaveChangesAsync();

        await AssertChoiceRejected(otherLine.Id, 3, 787);
        await AssertChoiceRejected(firstChoice.TargetLineId, firstChoice.Position, 2067);

        async Task AssertChoiceRejected(int targetLineId, int position, int expectedErrorCode)
        {
            using var attempt = app.Services.CreateScope();
            var context = attempt.ServiceProvider.GetRequiredService<DialogueDbContext>();
            context.Choices.Add(new DialogueChoice
            {
                DialogueId = firstChoice.DialogueId,
                SourceLineId = firstChoice.SourceLineId,
                TargetLineId = targetLineId,
                Text = "Недопустимый вариант ответа",
                Position = position
            });

            var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(expectedErrorCode, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
    }
}
