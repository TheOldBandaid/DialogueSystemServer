using System.Text.Json;
using DialogueSystem.Web.Models;
using static System.Net.HttpStatusCode;

namespace DialogueSystem.Tests;

public sealed class DialogueStoryStateTests
{
    private const string Dialogues = "/api/dialogues";
    private readonly string password = Guid.NewGuid().ToString("N");

    [Theory]
    [InlineData("Предупредить совет", "совет", "Позвать стражу")]
    [InlineData("Оставить сигнал в тайне", "тайна", "Подняться через причал")]
    public async Task CarriesDecisionThroughSharedSceneAndRecordsIt(string decision, string state, string availableAnswer)
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);
        var route = await Import(editor, await LoadExample());
        var report = await editor.Get<DialogueValidation>($"{route}/validation");
        Assert.Empty(report.Issues);
        Assert.Empty(report.UnreachableLineIds);

        var exported = await editor.Get<DialogueDocument>($"{route}/export");
        Assert.Contains(exported.Choices, choice => choice.Text == decision && choice.NextState == state);
        Assert.Contains(exported.Choices, choice => choice.Text == availableAnswer && choice.RequiredState == state);

        var opening = await editor.Read<PreviewStep>(HttpMethod.Post, $"{route}/previews");
        Assert.Null(opening.StoryState);
        var advance = $"{route}/previews/{opening.PreviewId}/choices";
        var first = Assert.Single(opening.Choices, choice => choice.Text == decision);
        var branch = await editor.Read<PreviewStep>(HttpMethod.Post, advance, payload: new SelectChoice(first.Id, 0));
        Assert.Equal(state, branch.StoryState);
        var shared = await editor.Read<PreviewStep>(HttpMethod.Post, advance,
            payload: new SelectChoice(Assert.Single(branch.Choices).Id, 1));
        Assert.Contains("у сигнальной башни", shared.Line.Text, StringComparison.Ordinal);
        Assert.Equal(state, shared.StoryState);

        var context = await editor.Get<LineContext>($"{route}/lines/{shared.Line.Id}/context");
        Assert.Equal(2, context.Outgoing.Count);
        var unavailable = Assert.Single(context.Outgoing, choice => choice.RequiredState != state);
        await editor.Expect(BadRequest, HttpMethod.Post, advance, new SelectChoice(unavailable.Id, shared.Step));
        var permitted = Assert.Single(shared.Choices);
        Assert.Equal(availableAnswer, permitted.Text);
        var final = await editor.Read<PreviewStep>(HttpMethod.Post, advance, payload: new SelectChoice(permitted.Id, shared.Step));
        Assert.True(final.Line.IsEnding);
        Assert.Equal(3, final.Step);

        var history = await editor.Get<PreviewHistory>($"{route}/previews/{opening.PreviewId}/history");
        Assert.Equal(new string?[] { null, state, state, state }, history.Steps.Select(step => step.StoryState));
        Assert.Equal(availableAnswer, history.Steps[3].ChoiceText);
    }

    [Fact]
    public async Task FindsDeadEndForOneStateAndAcceptsTheDialogueAfterRepair()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);

        var example = await LoadExample();
        var broken = example with
        {
            Choices = example.Choices.Select(choice => choice.RequiredState == "тайна"
                ? choice with { RequiredState = "потеряно" } : choice).ToArray()
        };
        var route = await Import(editor, broken);
        var exported = await editor.Get<DialogueDocument>($"{route}/export");
        var shared = Assert.Single(exported.Lines, line => line.Text.Contains("у сигнальной башни", StringComparison.Ordinal));
        var hiddenEnding = Assert.Single(exported.Lines, line => line.Text.Contains("поднялись", StringComparison.Ordinal));
        var report = await editor.Get<DialogueValidation>($"{route}/validation");
        Assert.Contains(report.Issues, issue => issue.Code == "state-no-path-to-ending" && issue.LineId == shared.Id
            && issue.Message.Contains("«тайна»", StringComparison.Ordinal));
        Assert.Contains(hiddenEnding.Id, report.UnreachableLineIds);
        await editor.Expect(UnprocessableEntity, HttpMethod.Post, $"{route}/previews");

        var context = await editor.Get<LineContext>($"{route}/lines/{shared.Id}/context");
        var answer = Assert.Single(context.Outgoing, choice => choice.RequiredState == "потеряно");
        await editor.Expect(NoContent, HttpMethod.Put, $"{route}/lines/{shared.Id}/choices/{answer.Id}",
            new { targetLineId = answer.Line.Id, answer.Text, answer.Position, requiredState = " тайна " });

        var repaired = await editor.Get<DialogueValidation>($"{route}/validation");
        Assert.Empty(repaired.Issues);
        Assert.Empty(repaired.UnreachableLineIds);
        await editor.Expect(OK, HttpMethod.Post, $"{route}/previews");
    }

    [Fact]
    public async Task StopsExistingPreviewIfAnEditRemovesItsOnlyAvailableExit()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start(settings: [new("DialogueSystem:EditorPassword", password)]);
        using var editor = new DialogueApiClient(app);
        await editor.Login("editor", password);

        var route = await Import(editor, await LoadExample());
        var first = await editor.Read<PreviewStep>(HttpMethod.Post, $"{route}/previews");
        var advance = $"{route}/previews/{first.PreviewId}/choices";
        var secret = Assert.Single(first.Choices, choice => choice.Text == "Оставить сигнал в тайне");
        var branch = await editor.Read<PreviewStep>(HttpMethod.Post, advance, payload: new SelectChoice(secret.Id, 0));
        var shared = await editor.Read<PreviewStep>(HttpMethod.Post, advance,
            payload: new SelectChoice(Assert.Single(branch.Choices).Id, 1));

        var opening = await editor.Get<LineContext>($"{route}/lines/{first.Line.Id}/context");
        var editedChoice = Assert.Single(opening.Outgoing, choice => choice.Id == secret.Id);
        await editor.Expect(NoContent, HttpMethod.Put, $"{route}/lines/{first.Line.Id}/choices/{editedChoice.Id}",
            new { targetLineId = editedChoice.Line.Id, editedChoice.Text, editedChoice.Position, nextState = "совет" });
        var sharedContext = await editor.Get<LineContext>($"{route}/lines/{shared.Line.Id}/context");
        var removedExit = Assert.Single(sharedContext.Outgoing, choice => choice.RequiredState == "тайна");
        await editor.Expect(NoContent, HttpMethod.Put, $"{route}/lines/{shared.Line.Id}/choices/{removedExit.Id}",
            new { targetLineId = removedExit.Line.Id, removedExit.Text, removedExit.Position, requiredState = "другое" });

        Assert.Empty((await editor.Get<DialogueValidation>($"{route}/validation")).Issues);
        var blocked = await editor.Read<DialogueValidation>(HttpMethod.Post, advance, UnprocessableEntity,
            new SelectChoice(removedExit.Id, shared.Step));
        Assert.Contains(blocked.Issues, issue => issue.Code == "state-no-path-to-ending" && issue.LineId == shared.Line.Id);
        var history = await editor.Get<PreviewHistory>($"{route}/previews/{first.PreviewId}/history");
        Assert.Equal(shared.Step, history.CurrentStep);
        Assert.Equal("тайна", history.Steps[^1].StoryState);
    }

    private static async Task<string> Import(DialogueApiClient editor, DialogueDocument document)
    {
        var dialogue = await editor.Read<DialogueSummary>(HttpMethod.Post, $"{Dialogues}/import", Created, document);
        return $"{Dialogues}/{dialogue.Id}";
    }

    private static async Task<DialogueDocument> LoadExample()
    {
        var filename = Path.Combine(AppContext.BaseDirectory, "examples", "duty-signal.json");
        await using var file = File.OpenRead(filename);
        return (await JsonSerializer.DeserializeAsync<DialogueDocument>(file, JsonSerializerOptions.Web))!;
    }
}
