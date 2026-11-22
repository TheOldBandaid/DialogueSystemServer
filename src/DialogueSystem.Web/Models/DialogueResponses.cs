using System.ComponentModel.DataAnnotations;

namespace DialogueSystem.Web.Models;

public sealed record DialogueSummary(int Id, string Title, int? StartLineId, int? MinimumPlayerLevel = null);

public sealed record LineSummary(int Id, string Speaker, string Text, bool IsEnding, bool IsStoryBranch, int SpeakerId);

public sealed record ChoiceLink(int Id, string Text, int Position, LineSummary Line);

public sealed record LineContext(
    LineSummary Line,
    IReadOnlyList<ChoiceLink> Incoming,
    IReadOnlyList<ChoiceLink> Outgoing);

public sealed record DialogueValidation(IReadOnlyList<string> Errors, IReadOnlyList<int> UnreachableLineIds)
{
    public IReadOnlyList<DialogueValidationIssue> Issues { get; init; } = [];
}

public sealed record DialogueValidationIssue(string Code, string Message, int? LineId = null);

public sealed record PreviewChoice(int Id, string Text);

public sealed record PreviewStep(
    Guid PreviewId,
    int Step,
    LineSummary Line,
    IReadOnlyList<PreviewChoice> Choices,
    IReadOnlyList<int> UnreachableLineIds);

public sealed record PreviewInput([Range(1, int.MaxValue)] int? PlayerLevel);

public sealed record SelectChoice(
    [Range(1, int.MaxValue)] int ChoiceId,
    [Required, Range(0, int.MaxValue)] int? Step);
