using DialogueSystem.Application;
using DialogueSystem.Domain;

namespace DialogueSystem.Tests;

public sealed class DialogueValidationLimitsTests
{
    [Fact]
    public void StopsStateExpansionAndDoesNotReportUnvisitedLinesAsUnreachable()
    {
        var dialogue = Create(150);
        var choices = new List<DialogueChoice>();
        for (var i = 1; i <= 150; i++)
        {
            choices.Add(Choice(1, 2, i, next: $"state-{i}"));
            choices.Add(Choice(i, i == 150 ? 1 : i + 1, 151));
        }
        choices.Add(Choice(1, 2, 152, required: "state-1"));
        var result = new DialogueGraph(dialogue, choices).Validate();
        Assert.False(result.Complete);
        Assert.Equal("validation-limit", Assert.Single(result.Issues).Code);
        Assert.Empty(result.UnreachableLineIds);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void LimitsErrorOutputWithoutAcceptingAnInvalidGraph()
    {
        var dialogue = Create(200);
        var choices = Enumerable.Range(2, 199).Select(id => Choice(1, id, id)).ToArray();
        var result = new DialogueGraph(dialogue, choices).Validate();
        Assert.True(result.Complete);
        Assert.Equal(DialogueLimits.MaximumValidationIssues, result.Issues.Count);
        Assert.Equal("issues-truncated", result.Issues[^1].Code);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void ChecksADetachedPreviewEvenWithoutConditionalChoices()
    {
        var dialogue = Create(3);
        dialogue.Lines[1].IsEnding = true;
        var graph = new DialogueGraph(dialogue, [Choice(1, 2, 1), Choice(3, 3, 1)]);
        Assert.Empty(graph.Validate().Errors);
        var result = graph.Validate(new DialoguePreview { CurrentLineId = 3 });
        Assert.Contains(result.Issues, issue => issue.Code == "no-path-to-ending" && issue.LineId == 3);
    }

    private static Dialogue Create(int count)
    {
        var speaker = new Character { Id = 1, Name = "Персонаж" };
        var dialogue = new Dialogue { Id = 1, Title = "Проверка ограничений", StartLineId = 1 };
        dialogue.Lines.AddRange(Enumerable.Range(1, count).Select(id => new DialogueLine
        { Id = id, DialogueId = 1, Speaker = speaker, Text = "Реплика" }));
        return dialogue;
    }

    private static DialogueChoice Choice(int source, int target, int position,
        string? next = null, string? required = null) => new()
    {
        DialogueId = 1, SourceLineId = source, TargetLineId = target, Position = position,
        Text = "Ответ", NextState = next, RequiredState = required
    };
}
