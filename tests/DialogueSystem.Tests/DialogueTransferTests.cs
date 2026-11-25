using DialogueSystem.Application;
using DialogueSystem.Domain;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueTransferTests
{
    private const string Dialogues = "/api/dialogues";
    private const string Import = Dialogues + "/import";
    private const string Characters = "/api/characters";
    private readonly string password = Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ReimportsExportWithIndependentLinesAndPreservesTheGraph()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var guest = new DialogueApiClient(app);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);

        var before = await editor.Get<DialogueSummary[]>(Dialogues);
        var charactersBefore = await editor.Get<Character[]>(Characters);
        var original = Assert.Single(before, item => item.Title == "Ключ от архива");
        var exportPath = $"{Dialogues}/{original.Id}/export";
        await guest.Expect(Unauthorized, HttpMethod.Get, exportPath);
        await editor.Expect(NotFound, HttpMethod.Get, $"{Dialogues}/0/export");
        var document = await editor.Get<DialogueDocument>(exportPath);
        Assert.Equal(2, document.Version);
        await guest.Expect(Unauthorized, HttpMethod.Post, Import, document);
        await editor.Expect(BadRequest, HttpMethod.Post, Import, document, includeToken: false);
        Assert.Equal(before, await editor.Get<DialogueSummary[]>(Dialogues));

        var copied = await editor.Read<DialogueSummary>(HttpMethod.Post, Import, Created,
            document with { Title = "Копия архива" });
        Assert.NotEqual(original.Id, copied.Id);
        Assert.Equal("Копия архива", copied.Title);
        var copyPath = $"{Dialogues}/{copied.Id}";
        var copy = await editor.Get<DialogueDocument>($"{copyPath}/export");
        Assert.Equal(document.Lines.Count, copy.Lines.Count);
        Assert.Empty(copy.Lines.Select(line => line.Id).Intersect(document.Lines.Select(line => line.Id)));

        var lineIds = document.Lines.ToDictionary(line => line.Id, line =>
        {
            var saved = Assert.Single(copy.Lines, item => item.Text == line.Text);
            Assert.Equal(line with { Id = saved.Id }, saved);
            return saved.Id;
        });
        Assert.Equal(lineIds[Assert.IsType<int>(document.StartLineId)], copy.StartLineId);
        var expectedChoices = document.Choices.Select(choice => choice with
        {
            SourceLineId = lineIds[choice.SourceLineId],
            TargetLineId = lineIds[choice.TargetLineId]
        }).OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position);
        Assert.Equal(expectedChoices, copy.Choices);
        var charactersAfter = await editor.Get<Character[]>(Characters);
        Assert.Equal(charactersBefore.Select(character => character.Id), charactersAfter.Select(character => character.Id));

        var preview = await editor.Read<PreviewStep>(HttpMethod.Post, $"{copyPath}/previews");
        Assert.Equal(copy.StartLineId, preview.Line.Id);
        var copyStart = Assert.Single(copy.Lines, line => line.Id == copy.StartLineId);
        await editor.Expect(NoContent, HttpMethod.Put, $"{copyPath}/lines/{copyStart.Id}",
            new { speakerId = preview.Line.SpeakerId, text = "Новый текст копии" });
        var unchanged = await editor.Get<DialogueDocument>(exportPath);
        Assert.Equal(document.Lines.ToArray(), unchanged.Lines.ToArray());
        Assert.Equal(document.Choices.ToArray(), unchanged.Choices.ToArray());
    }

    [Fact]
    public async Task ImportsDraftAndCreatesOnlyMissingCharacters()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings:
        [
            new("DialogueSystem:EditorPassword", password),
            new("DialogueSystem:SeedDemo", "false")
        ]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);

        var draft = new DialogueDocument(1, "Черновик", null,
            [new(38, "Стражник", "Кто идёт?", false, true), new(17, "Стражник", "Стоять!", true, false)],
            [new(38, 17, "Остановиться", 1)]);
        var empty = await editor.Read<DialogueSummary>(HttpMethod.Post, Import, Created,
            draft with { Title = "Пустой диалог", Lines = [], Choices = [] });
        var emptyExport = await editor.Get<DialogueDocument>($"{Dialogues}/{empty.Id}/export");
        Assert.Empty(emptyExport.Lines);
        Assert.Empty(emptyExport.Choices);

        var imported = await editor.Read<DialogueSummary>(HttpMethod.Post, Import, Created, draft);
        Assert.Null(imported.StartLineId);
        await editor.Expect(UnprocessableEntity, HttpMethod.Post, $"{Dialogues}/{imported.Id}/previews");

        await editor.Read<DialogueSummary>(HttpMethod.Post, Import, Created, draft);
        Assert.Equal("Стражник", Assert.Single(await editor.Get<Character[]>(Characters)).Name);
        var saved = await editor.Get<DialogueDocument>($"{Dialogues}/{imported.Id}/export");
        var link = Assert.Single(saved.Choices);
        Assert.Equal("Кто идёт?", Assert.Single(saved.Lines, line => line.Id == link.SourceLineId).Text);
        Assert.Equal("Стоять!", Assert.Single(saved.Lines, line => line.Id == link.TargetLineId).Text);
    }

    [Theory]
    [InlineData("duplicate-line")]
    [InlineData("unknown-start")]
    [InlineData("unknown-target")]
    [InlineData("duplicate-position")]
    [InlineData("ending-choice")]
    public async Task RejectsInvalidDocumentWithoutSavingDialogueOrCharacter(string issue)
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);

        var start = new DialogueDocumentLine(1, "Новый персонаж", "Начало", false, false);
        var ending = new DialogueDocumentLine(5, "Новый персонаж", "Конец", true, false);
        var choice = new DialogueDocumentChoice(1, 5, "Продолжить", 1);
        var document = new DialogueDocument(1, "Загрузка с ошибкой", 1, [start, ending], [choice]);
        var invalid = issue switch
        {
            "duplicate-line" => document with { Lines = [start, ending with { Id = 1 }] },
            "unknown-start" => document with { StartLineId = 3 },
            "unknown-target" => document with { Choices = [choice with { TargetLineId = 3 }] },
            "duplicate-position" => document with { Choices = [choice, choice with { Text = "Другой ответ" }] },
            "ending-choice" => document with { Choices = [choice with { SourceLineId = 5 }] },
            _ => throw new ArgumentOutOfRangeException(nameof(issue))
        };

        var before = await editor.Get<DialogueSummary[]>(Dialogues);
        var charactersBefore = await editor.Get<Character[]>(Characters);
        await editor.Expect(BadRequest, HttpMethod.Post, Import, invalid);
        Assert.Equal(before, await editor.Get<DialogueSummary[]>(Dialogues));
        var charactersAfter = await editor.Get<Character[]>(Characters);
        Assert.Equal(charactersBefore.Select(character => character.Id), charactersAfter.Select(character => character.Id));
    }
}
