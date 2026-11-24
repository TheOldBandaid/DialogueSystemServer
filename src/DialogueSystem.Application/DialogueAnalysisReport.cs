namespace DialogueSystem.Application;

public sealed record DialogueAnalysisReport(
    string Title,
    int? StartLineId,
    int? MinimumPlayerLevel,
    bool Complete,
    int LineCount,
    int ReachableLineCount,
    int VisitedPositionCount,
    int ChoiceCount,
    int ReachableChoiceCount,
    int? UnreachableLineCount,
    int? UnavailableChoiceCount,
    int? TrappedPositionCount,
    IReadOnlyList<DialogueEndingAnalysis> Endings,
    IReadOnlyList<DialogueLineReference> UnreachableLines,
    IReadOnlyList<UnavailableDialogueChoice> UnavailableChoices,
    IReadOnlyList<TrappedDialoguePosition> TrappedPositions);

public sealed record DialogueLineReference(int Id, string Speaker, string Text);

public sealed record DialogueEndingAnalysis(DialogueLineReference Line, int? Steps, bool? Reachable);

public sealed record UnavailableDialogueChoice(
    int SourceLineId, int TargetLineId, int Position, string Text, string? RequiredState, string Reason);

public sealed record TrappedDialoguePosition(int LineId, string? StoryState);

public sealed record DialogueRouteStep(int SourceLineId, int TargetLineId, int Position,
    string Answer, string? RequiredState, string? StateBefore, string? StateAfter);

public sealed record DialogueEndingRoute(int StartLineId, int EndingLineId,
    string? FinalState, IReadOnlyList<DialogueRouteStep> Steps);

public sealed record DialogueRouteResult(bool Complete, DialogueEndingRoute? Route);
