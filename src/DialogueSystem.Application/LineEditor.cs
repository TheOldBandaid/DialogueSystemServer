namespace DialogueSystem.Application;

public sealed record LineChange(int SpeakerId, string Text, bool IsEnding, bool IsStoryBranch, string? Version = null);
public sealed record ChoiceChange(
    int TargetLineId, string Text, int Position, string? RequiredState, string? NextState, string? Version = null);

public enum EditStatus { Saved, NotFound, Invalid, Conflict, StaleVersion }

public record EditResult(EditStatus Status, string? Detail = null);

public sealed record EditResult<T>(EditStatus Status, string? Detail = null, T? Value = default)
    : EditResult(Status, Detail);

public interface ILineEditor
{
    Task<LineResource?> ReadAsync(int id, int lineId, CancellationToken ct);
    Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null);
    Task<CatalogPage<LineReference>?> PageAsync(int id, string? query, int? before, bool branchesOnly, CancellationToken ct);
    Task<ChoiceSummary?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct);
    Task<EditResult<LineSummary>> CreateAsync(int id, LineChange input, CancellationToken ct);
    Task<EditResult> UpdateAsync(int id, int lineId, LineChange input, CancellationToken ct);
    Task<EditResult> DeleteAsync(int id, int lineId, string version, CancellationToken ct);
    Task<EditResult<ChoiceSummary>> SaveChoiceAsync(int id, int lineId, int? choiceId, ChoiceChange input, CancellationToken ct);
    Task<EditResult> DeleteChoiceAsync(int id, int lineId, int choiceId, string version, CancellationToken ct);
    Task<EditResult> MoveChoiceAsync(int id, int lineId, int choiceId, int direction, string version, CancellationToken ct);
}
