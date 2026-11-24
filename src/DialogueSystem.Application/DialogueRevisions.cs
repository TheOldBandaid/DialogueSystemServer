namespace DialogueSystem.Application;

public sealed record DialogueRevisionSummary(
    int Id, string Label, string Author, long CreatedAtUnixMilliseconds, int LineCount, int ChoiceCount);

public sealed record DialogueRevisionPage(IReadOnlyList<DialogueRevisionSummary> Items, int? NextBeforeId);

public interface IDialogueRevisions
{
    Task<DialogueRevisionPage?> ListAsync(int dialogueId, int? beforeId, CancellationToken ct);
    Task<DialogueRevisionSummary?> CreateAsync(int dialogueId, string label, string author, CancellationToken ct);
    Task<DialogueDocument?> ExportAsync(int dialogueId, int revisionId, CancellationToken ct);
    Task<DialogueSummary?> CopyAsync(int dialogueId, int revisionId, CancellationToken ct);
}
