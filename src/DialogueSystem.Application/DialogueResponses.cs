using System.ComponentModel.DataAnnotations;

namespace DialogueSystem.Application;

public sealed record DialogueSummary(int Id, string Title, int? StartLineId, int? MinimumPlayerLevel = null);

public sealed record DialogueOverview(int Id, string Title, bool HasStart, int? MinimumPlayerLevel,
    int LineCount, int BranchCount, int EndingCount);

public sealed record LineSummary(int Id, string Speaker, string Text, bool IsEnding, bool IsStoryBranch, int SpeakerId)
{
    public string Version => EditVersions.Line(SpeakerId, Text, IsEnding, IsStoryBranch);
}

public sealed record ChoiceLink(
    int Id, string Text, int Position, LineSummary Line, string? RequiredState = null, string? NextState = null,
    int TargetLineId = 0)
{
    public string Version => EditVersions.Choice(TargetLineId, Text, Position, RequiredState, NextState);
}

public sealed record LineContext(
    LineSummary Line,
    IReadOnlyList<ChoiceLink> Incoming,
    IReadOnlyList<ChoiceLink> Outgoing);

public sealed record DialogueValidation(IReadOnlyList<string> Errors, IReadOnlyList<int> UnreachableLineIds)
{
    public IReadOnlyList<DialogueValidationIssue> Issues { get; init; } = [];
    public bool Complete { get; init; } = true;
}

public sealed record DialogueValidationIssue(string Code, string Message, int? LineId = null);

public sealed record PreviewChoice(int Id, string Text);

public sealed record PreviewStep(
    Guid PreviewId,
    int Step,
    LineSummary Line,
    IReadOnlyList<PreviewChoice> Choices,
    IReadOnlyList<int> UnreachableLineIds,
    string? StoryState = null);

public sealed record PreviewSessionSummary(Guid PreviewId, int Step, long? StartedAtUnixMilliseconds);
public sealed record PreviewSessionPage(IReadOnlyList<PreviewSessionSummary> Items, Guid? NextBeforeId);

public sealed record PreviewHistoryStep(
    int Step, int LineId, string Speaker, string Text, bool IsEnding, string? ChoiceText, string? StoryState = null);

public sealed record PreviewHistory(Guid PreviewId, int CurrentStep, IReadOnlyList<PreviewHistoryStep> Steps)
{
    public int? NextAfterStep { get; init; }
}

public sealed record PreviewInput([Range(1, int.MaxValue)] int? PlayerLevel);

public sealed record SelectChoice(
    [Range(1, int.MaxValue)] int ChoiceId,
    [Required, Range(0, int.MaxValue)] int? Step);
