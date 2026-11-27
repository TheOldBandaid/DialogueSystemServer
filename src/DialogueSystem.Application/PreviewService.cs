namespace DialogueSystem.Application;

public enum PreviewStatus { Ready, NotFound, InvalidGraph, LevelTooLow, StaleStep, UnavailableChoice, StaleGraph, RemovedLine }

public sealed record PreviewResult(PreviewStatus Status, PreviewStep? Step = null,
    DialogueValidation? Validation = null, int? MinimumLevel = null);

public interface IPreviewService
{
    Task<PreviewSessionPage?> ListPageAsync(int dialogueId, int userId, Guid? before, CancellationToken ct);
    Task<PreviewHistory?> HistoryAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct, int after = -1);
    Task<PreviewResult> StartAsync(int dialogueId, int userId, int? playerLevel, CancellationToken ct);
    Task<PreviewResult> AdvanceAsync(int dialogueId, int userId, Guid previewId, SelectChoice request, CancellationToken ct);
}
