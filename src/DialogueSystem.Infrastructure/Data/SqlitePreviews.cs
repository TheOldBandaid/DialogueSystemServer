using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqlitePreviews(DialogueDbContext db, GraphSnapshots cache) : IPreviewService
{
    public async Task<PreviewSessionPage?> ListPageAsync(int dialogueId, int userId, Guid? before, CancellationToken ct)
    {
        var selected = Owned(dialogueId, userId);
        if (before is Guid cursorId)
        {
            var cursor = await selected.Where(preview => preview.Id == cursorId)
                .Select(preview => new { preview.Id, preview.StartedAtUnixMilliseconds }).SingleOrDefaultAsync(ct);
            if (cursor is null) return null;
            var key = cursor.Id.ToString().ToUpperInvariant();
            selected = cursor.StartedAtUnixMilliseconds is long timestamp
                ? selected.Where(preview => preview.StartedAtUnixMilliseconds < timestamp ||
                    preview.StartedAtUnixMilliseconds == null ||
                    preview.StartedAtUnixMilliseconds == timestamp && string.Compare(preview.Id.ToString(), key) < 0)
                : selected.Where(preview => preview.StartedAtUnixMilliseconds == null && string.Compare(preview.Id.ToString(), key) < 0);
        }
        var items = await selected.OrderByDescending(preview => preview.StartedAtUnixMilliseconds)
            .ThenByDescending(preview => preview.Id).Take(21)
            .Select(preview => new PreviewSessionSummary(preview.Id, preview.Step, preview.StartedAtUnixMilliseconds))
            .ToListAsync(ct);
        if (items.Count == 0 && !await db.Dialogues.AnyAsync(dialogue => dialogue.Id == dialogueId, ct)) return null;
        var next = items.Count > 20 ? items[19].PreviewId : (Guid?)null;
        if (next.HasValue) items.RemoveAt(20);
        return new(items, next);
    }

    public async Task<PreviewHistory?> HistoryAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct, int after = -1)
    {
        const int pageSize = 100;
        var result = await Owned(dialogueId, userId).AsSingleQuery()
            .Where(preview => preview.Id == previewId)
            .Select(preview => new PreviewHistory(preview.Id, preview.Step,
                preview.History.Where(entry => entry.Step > after).OrderBy(entry => entry.Step).Take(pageSize + 1)
                    .Select(entry => new PreviewHistoryStep(
                        entry.Step, entry.LineId, entry.Speaker, entry.Text, entry.IsEnding, entry.ChoiceText,
                        entry.StoryState)).ToList()))
            .SingleOrDefaultAsync(ct);
        return result is null || result.Steps.Count <= pageSize ? result :
            result with { Steps = result.Steps.Take(pageSize).ToArray(), NextAfterStep = result.Steps[pageSize - 1].Step };
    }

    private Task<DialoguePreview?> FindAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        Owned(dialogueId, userId).SingleOrDefaultAsync(preview => preview.Id == previewId, ct);

    private Task<DialogueGraph?> LoadGraphAsync(int dialogueId, CancellationToken ct) =>
        DialogueGraphLoader.LoadAsync(db, cache, dialogueId, ct);

    private Task<bool> GraphIsCurrentAsync(int dialogueId, long revision, CancellationToken ct) =>
        db.Dialogues.AnyAsync(dialogue => dialogue.Id == dialogueId && dialogue.Revision == revision, ct);

    private Task<int?> CurrentStepAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        Owned(dialogueId, userId).Where(preview => preview.Id == previewId)
            .Select(preview => (int?)preview.Step).SingleOrDefaultAsync(ct);

    private IQueryable<DialoguePreview> Owned(int dialogueId, int userId) =>
        db.Previews.Where(preview => preview.DialogueId == dialogueId && preview.UserId == userId);
    public async Task<PreviewResult> StartAsync(int dialogueId, int userId, int? playerLevel, CancellationToken ct)
    {
        var graph = await LoadGraphAsync(dialogueId, ct);
        if (graph is null)
            return new(PreviewStatus.NotFound);

        var validation = graph.Validate(ct: ct);
        if (validation.Errors.Count != 0)
            return new(PreviewStatus.InvalidGraph, Validation: validation);

        if (graph.MinimumPlayerLevel is int minimum && (playerLevel is null || playerLevel < minimum))
            return new(PreviewStatus.LevelTooLow, MinimumLevel: minimum);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await GraphIsCurrentAsync(dialogueId, graph.Revision, ct))
            return new(PreviewStatus.StaleGraph);

        var preview = new DialoguePreview
        {
            Id = Guid.NewGuid(),
            DialogueId = dialogueId,
            CurrentLineId = graph.StartLineId!.Value,
            UserId = userId,
            StartedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        db.Previews.Add(preview);
        var step = RecordStep(graph, preview, validation.UnreachableLineIds, choiceText: null);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(PreviewStatus.Ready, Step: step);
    }

    public async Task<PreviewResult> AdvanceAsync(
        int dialogueId, int userId, Guid previewId, SelectChoice request, CancellationToken ct)
    {
        var preview = await FindAsync(dialogueId, userId, previewId, ct);
        if (preview is null)
            return new(PreviewStatus.NotFound);

        if (request.Step != preview.Step)
            return new(PreviewStatus.StaleStep);

        var graph = await LoadGraphAsync(dialogueId, ct);
        if (graph is null) return new(PreviewStatus.NotFound);
        if (!graph.ContainsLine(preview.CurrentLineId)) return new(PreviewStatus.RemovedLine);
        var validation = graph.Validate(preview, ct);
        if (validation.Errors.Count != 0)
            return new(PreviewStatus.InvalidGraph, Validation: validation);

        var choice = graph.FindChoice(preview, request.ChoiceId);
        if (choice is null)
            return new(PreviewStatus.UnavailableChoice);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var currentStep = await CurrentStepAsync(dialogueId, userId, previewId, ct);
        if (currentStep is null) return new(PreviewStatus.NotFound);
        if (currentStep != request.Step) return new(PreviewStatus.StaleStep);
        if (!await GraphIsCurrentAsync(dialogueId, graph.Revision, ct))
            return new(PreviewStatus.StaleGraph);

        preview.CurrentLineId = choice.TargetLineId;
        preview.StoryState = DialogueStoryRules.Advance(choice.NextState, preview.StoryState);
        preview.Step++;
        var step = RecordStep(graph, preview, validation.UnreachableLineIds, choice.Text);
        await db.SaveChangesAsync(ct);
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
