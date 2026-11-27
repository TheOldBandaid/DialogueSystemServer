namespace DialogueSystem.Application;

public interface IDialogueCatalog
{
    Task<IReadOnlyList<DialogueSummary>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<DialogueOverview>> OverviewAsync(CancellationToken ct);
    Task<CatalogPage<DialogueOverview>> PageAsync(string? query, bool? hasStart, int? before, CancellationToken ct);
    Task<DialogueSummary?> FindAsync(int id, CancellationToken ct);
    Task<LineResource?> FindLineAsync(int id, int lineId, CancellationToken ct);
    Task<DialogueSummary> CreateAsync(string title, CancellationToken ct);
    Task<ChangeStatus> RenameAsync(int id, string title, string version, CancellationToken ct);
    Task<ChangeStatus> DeleteAsync(int id, string version, CancellationToken ct);
    Task<ChangeStatus> SetMinimumLevelAsync(int id, int? minimum, string version, CancellationToken ct);
    Task<DialogueDocument?> ExportAsync(int id, CancellationToken ct);
    Task<DialogueMapSnapshot?> MapAsync(int id, CancellationToken ct);
    Task<DialogueSummary> ImportAsync(DialogueDocument document, CancellationToken ct);
    Task<DialogueSummary?> CopyAsync(int id, bool cloneCharacters, CancellationToken ct);
    Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null);
    Task<IReadOnlyList<LineSummary>?> BranchesAsync(int id, CancellationToken ct);
    Task<DialogueValidation?> ValidateAsync(int id, CancellationToken ct);
}

public interface ICharacterCatalog
{
    Task<IReadOnlyList<CharacterSummary>> ListAsync(CancellationToken ct);
    Task<CatalogPage<CharacterSummary>> PageAsync(string? query, int? before, int? except, CancellationToken ct);
    Task<CharacterSummary?> FindAsync(int id, CancellationToken ct);
    Task<CharacterSummary> CreateAsync(string name, CancellationToken ct);
    Task<ChangeStatus> RenameAsync(int id, string name, string version, CancellationToken ct);
    Task<CharacterUsage?> UsageAsync(int id, CancellationToken ct, int? before = null);
    Task<CharacterRemoval> DeleteAsync(int id, string version, CancellationToken ct);
    Task<CharacterReassignment> ReassignAsync(int sourceId, int targetId, int? dialogueId, string version, string targetVersion, CancellationToken ct);
}

public enum ChangeStatus { Saved, NotFound, Conflict }
public sealed record CharacterSummary(int Id, string Key, string Name, long Revision)
{
    public string Version => EditVersions.Character(Id, Revision);
}
public sealed record CharacterUsage(int Id, string Name, int LineCount, IReadOnlyList<CharacterDialogueUsage> Dialogues)
{
    public string Version { get; init; } = "";
    public int DialogueCount { get; init; }
    public int? NextBeforeId { get; init; }
}

public sealed record CharacterDialogueUsage(int DialogueId, string Title, int FirstLineId, int LineCount);

public enum CharacterRemoval { Removed, NotFound, InUse, Conflict }

public enum ReassignmentStatus { Reassigned, SameCharacter, SourceNotFound, TargetNotFound, DialogueNotFound, Conflict }

public sealed record CharacterReassignment(ReassignmentStatus Status, int UpdatedLineCount = 0);

public sealed record ReassignedLines(int UpdatedLineCount);
public sealed record DialogueCopyInput(bool CloneCharacters = false);
