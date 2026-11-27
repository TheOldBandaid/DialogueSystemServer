namespace DialogueSystem.Application;

public sealed record DialogueMapSnapshot(int Id, long Revision, int? StartLineId,
    IReadOnlyList<DialogueMapLine> Lines, IReadOnlyList<DialogueMapChoice> Choices, bool Complete = true);
public sealed record DialogueMapLine(int Id, string Speaker, string Text, bool IsEnding, bool IsStoryBranch);
public sealed record DialogueMapChoice(int SourceLineId, int TargetLineId, string Text, int Position,
    string? RequiredState, string? NextState);
