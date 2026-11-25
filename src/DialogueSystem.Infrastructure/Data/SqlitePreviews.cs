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

    public Task<PreviewHistory?> HistoryAsync(int dialogueId, int userId, Guid previewId, CancellationToken ct) =>
        Owned(dialogueId, userId).AsSingleQuery()
            .Where(preview => preview.Id == previewId)
            .Select(preview => new PreviewHistory(preview.Id, preview.Step,
                preview.History.OrderBy(entry => entry.Step)
                    .Select(entry => new PreviewHistoryStep(
                        entry.Step, entry.LineId, entry.Speaker, entry.Text, entry.IsEnding, entry.ChoiceText,
                        entry.StoryState)).ToList()))
            .SingleOrDefaultAsync(ct);

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
