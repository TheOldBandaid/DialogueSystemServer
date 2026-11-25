using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public interface IPreviewStore
{
    Task<IReadOnlyList<PreviewSessionSummary>> ListAsync(int dialogueId, int userId, CancellationToken ct);
    Task<PreviewHistory?> HistoryAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct);
    Task<DialoguePreview?> FindAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct);
    Task<DialogueGraph?> LoadGraphAsync(int dialogueId, CancellationToken ct);
    Task<bool> GraphIsCurrentAsync(int dialogueId, long revision, CancellationToken ct);
    Task<int?> CurrentStepAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct);
    Task<IStorageTransaction> BeginAsync(CancellationToken ct);
    void Add(DialoguePreview preview);
    Task SaveAsync(CancellationToken ct);
}

public enum PreviewStatus { Ready, NotFound, InvalidGraph, LevelTooLow, StaleStep, UnavailableChoice, StaleGraph }

public sealed record PreviewResult(PreviewStatus Status, PreviewStep? Step = null,
    DialogueValidation? Validation = null, int? MinimumLevel = null);

public sealed class PreviewService(IPreviewStore store)
{
    public Task<IReadOnlyList<PreviewSessionSummary>> ListAsync(int dialogueId, int userId, CancellationToken ct) =>
        store.ListAsync(dialogueId, userId, ct);

    public Task<PreviewHistory?> HistoryAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        store.HistoryAsync(dialogueId, userId, previewId, ct);

    public async Task<PreviewResult> StartAsync(int dialogueId, int userId, int? playerLevel, CancellationToken ct)
    {
        var graph = await store.LoadGraphAsync(dialogueId, ct);
        if (graph is null)
            return new(PreviewStatus.NotFound);

        var validation = graph.Validate(ct: ct);
        if (validation.Errors.Count != 0)
            return new(PreviewStatus.InvalidGraph, Validation: validation);

        if (graph.MinimumPlayerLevel is int minimum && (playerLevel is null || playerLevel < minimum))
            return new(PreviewStatus.LevelTooLow, MinimumLevel: minimum);

        await using var transaction = await store.BeginAsync(ct);
        if (!await store.GraphIsCurrentAsync(dialogueId, graph.Revision, ct))
            return new(PreviewStatus.StaleGraph);

        var preview = new DialoguePreview
        {
            Id = Guid.NewGuid(),
            DialogueId = dialogueId,
            CurrentLineId = graph.StartLineId!.Value,
            UserId = userId,
            StartedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        store.Add(preview);
        var step = RecordStep(graph, preview, validation.UnreachableLineIds, choiceText: null);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PreviewStatus.Ready, Step: step);
    }

    public async Task<PreviewResult> AdvanceAsync(
        int dialogueId, int userId, Guid previewId, SelectChoice request, CancellationToken ct)
    {
        var preview = await store.FindAsync(dialogueId, userId, previewId, ct);
        if (preview is null)
            return new(PreviewStatus.NotFound);

        if (request.Step != preview.Step)
            return new(PreviewStatus.StaleStep);

        var graph = await store.LoadGraphAsync(dialogueId, ct);
        if (graph is null) return new(PreviewStatus.NotFound);
        var validation = graph.Validate(preview, ct);
        if (validation.Errors.Count != 0)
            return new(PreviewStatus.InvalidGraph, Validation: validation);

        var choice = graph.FindChoice(preview, request.ChoiceId);
        if (choice is null)
            return new(PreviewStatus.UnavailableChoice);

        await using var transaction = await store.BeginAsync(ct);
        var currentStep = await store.CurrentStepAsync(dialogueId, userId, previewId, ct);
        if (currentStep is null) return new(PreviewStatus.NotFound);
        if (currentStep != request.Step) return new(PreviewStatus.StaleStep);
        if (!await store.GraphIsCurrentAsync(dialogueId, graph.Revision, ct))
            return new(PreviewStatus.StaleGraph);

        preview.CurrentLineId = choice.TargetLineId;
        preview.StoryState = DialogueStoryRules.Advance(choice.NextState, preview.StoryState);
        preview.Step++;
        var step = RecordStep(graph, preview, validation.UnreachableLineIds, choice.Text);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PreviewStatus.Ready, Step: step);
    }

    private static PreviewStep RecordStep(
        DialogueGraph graph, DialoguePreview preview, IReadOnlyList<int> unreachable, string? choiceText)
    {
        var step = graph.GetPreviewStep(preview, unreachable);
        preview.History.Add(new PreviewHistoryEntry
        {
            Step = step.Step,
            LineId = step.Line.Id,
            Speaker = step.Line.Speaker,
            Text = step.Line.Text,
            IsEnding = step.Line.IsEnding,
            ChoiceText = choiceText,
            StoryState = step.StoryState
        });
        return step;
    }
}
