using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqlitePreviews(DialogueDbContext db, GraphSnapshots cache) : IPreviewStore
{
    public async Task<IReadOnlyList<PreviewSessionSummary>> ListAsync(int dialogueId, int userId, CancellationToken ct) =>
        await Owned(dialogueId, userId)
            .OrderByDescending(preview => preview.StartedAtUnixMilliseconds).ThenByDescending(preview => preview.Id)
            .Take(20)
            .Select(preview => new PreviewSessionSummary(preview.Id, preview.Step, preview.StartedAtUnixMilliseconds))
            .ToListAsync(ct);

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

    public Task<DialoguePreview?> FindAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        Owned(dialogueId, userId).SingleOrDefaultAsync(preview => preview.Id == previewId, ct);

    public Task<DialogueGraph?> LoadGraphAsync(int dialogueId, CancellationToken ct) =>
        DialogueGraphLoader.LoadAsync(db, cache, dialogueId, ct);

    public Task<bool> GraphIsCurrentAsync(int dialogueId, long revision, CancellationToken ct) =>
        db.Dialogues.AnyAsync(dialogue => dialogue.Id == dialogueId && dialogue.Revision == revision, ct);

    public Task<int?> CurrentStepAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        Owned(dialogueId, userId).Where(preview => preview.Id == previewId)
            .Select(preview => (int?)preview.Step).SingleOrDefaultAsync(ct);

    public async Task<IStorageTransaction> BeginAsync(CancellationToken ct) =>
        new SqliteTransaction(await db.Database.BeginTransactionAsync(ct));

    public void Add(DialoguePreview preview) => db.Previews.Add(preview);

    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);

    private IQueryable<DialoguePreview> Owned(int dialogueId, int userId) =>
        db.Previews.Where(preview => preview.DialogueId == dialogueId && preview.UserId == userId);
}
