using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public interface IDialogueCatalog
{
    Task<IReadOnlyList<DialogueSummary>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<DialogueOverview>> OverviewAsync(CancellationToken ct);
    Task<DialogueSummary?> FindAsync(int id, CancellationToken ct);
    Task<DialogueSummary> CreateAsync(string title, CancellationToken ct);
    Task<bool> RenameAsync(int id, string title, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
    Task<bool> SetMinimumLevelAsync(int id, int? minimum, CancellationToken ct);
    Task<DialogueDocument?> ExportAsync(int id, CancellationToken ct);
    Task<DialogueSummary> ImportAsync(DialogueDocument document, CancellationToken ct);
    Task<DialogueSummary?> CopyAsync(int id, bool cloneCharacters, CancellationToken ct);
    Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct);
    Task<IReadOnlyList<LineSummary>?> BranchesAsync(int id, CancellationToken ct);
    Task<DialogueValidation?> ValidateAsync(int id, CancellationToken ct);
}

public interface ICharacterCatalog
{
    Task<IReadOnlyList<Character>> ListAsync(CancellationToken ct);
    Task<Character?> FindAsync(int id, CancellationToken ct);
    Task<Character> CreateAsync(string name, CancellationToken ct);
    Task<bool> RenameAsync(int id, string name, CancellationToken ct);
    Task<CharacterUsage?> UsageAsync(int id, CancellationToken ct);
    Task<CharacterRemoval> DeleteAsync(int id, CancellationToken ct);
    Task<CharacterReassignment> ReassignAsync(int sourceId, int targetId, int? dialogueId, CancellationToken ct);
}

public sealed record CharacterUsage(int Id, string Name, int LineCount, IReadOnlyList<CharacterDialogueUsage> Dialogues);

public sealed record CharacterDialogueUsage(int DialogueId, string Title, int FirstLineId, int LineCount);

public enum CharacterRemoval { Removed, NotFound, InUse }

public enum ReassignmentStatus { Reassigned, SameCharacter, SourceNotFound, TargetNotFound, DialogueNotFound }

public sealed record CharacterReassignment(ReassignmentStatus Status, int UpdatedLineCount = 0);

public sealed record ReassignedLines(int UpdatedLineCount);
public sealed record DialogueCopyInput(bool CloneCharacters = false);
