using System.Text.Json;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteRevisions(DialogueDbContext db, GraphSnapshots cache) : IDialogueRevisions
{
    private const int PageSize = 20;

    public async Task<DialogueRevisionPage?> ListAsync(int dialogueId, int? beforeId, CancellationToken ct)
    {
        var revisions = db.DialogueRevisions.Where(revision => revision.DialogueId == dialogueId);
        if (beforeId is int cursor)
            revisions = revisions.Where(revision => revision.Id < cursor);

        var items = await revisions.OrderByDescending(revision => revision.Id).Take(PageSize + 1)
            .Select(revision => new DialogueRevisionSummary(revision.Id, revision.Label, revision.Author,
                revision.CreatedAtUnixMilliseconds, revision.LineCount, revision.ChoiceCount))
            .ToListAsync(ct);
        if (items.Count == 0 && !await db.Dialogues.AnyAsync(dialogue => dialogue.Id == dialogueId, ct))
            return null;

        var next = items.Count > PageSize ? items[PageSize - 1].Id : (int?)null;
        if (next.HasValue) items.RemoveAt(PageSize);
        return new(items, next);
    }

    public async Task<DialogueRevisionSummary?> CreateAsync(int dialogueId, string label, string author, CancellationToken ct)
    {
        var graph = await DialogueGraphLoader.LoadAsync(db, cache, dialogueId, ct);
        if (graph is null)
            return null;

        var document = graph.ToDocument();
        var revision = new DialogueRevision
        {
            DialogueId = dialogueId,
            Label = label.Trim(),
            Author = author,
            CreatedAtUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            LineCount = document.Lines.Count,
            ChoiceCount = document.Choices.Count,
            DocumentJson = JsonSerializer.Serialize(document, JsonSerializerOptions.Web)
        };
        ct.ThrowIfCancellationRequested();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Dialogues.Where(dialogue => dialogue.Id == dialogueId)
            .Select(dialogue => (long?)dialogue.Revision).SingleOrDefaultAsync(ct);
        if (current is null) return null;
        if (current != graph.Revision) throw new DialogueSnapshotConflictException();
        db.DialogueRevisions.Add(revision);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(revision.Id, revision.Label, revision.Author, revision.CreatedAtUnixMilliseconds,
            revision.LineCount, revision.ChoiceCount);
    }

    public async Task<DialogueDocument?> ExportAsync(int dialogueId, int revisionId, CancellationToken ct)
    {
        var revision = await FindAsync(dialogueId, revisionId, ct);
        return revision is null ? null : ReadDocument(revision);
    }

    public async Task<DialogueSummary?> CopyAsync(int dialogueId, int revisionId, CancellationToken ct, bool cloneCharacters = false)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var revision = await FindAsync(dialogueId, revisionId, ct);
        if (revision is null)
            return null;

        var document = ReadDocument(revision);
        var suffix = $" (версия «{revision.Label}»)";
        var title = document.Title.Length + suffix.Length <= 160 ? document.Title + suffix : revision.Label;
        var copy = await DialogueImporter.SaveWithinTransactionAsync(db, document with { Title = title }, ct, cloneCharacters);
        await transaction.CommitAsync(ct);
        return copy;
    }

    private Task<DialogueRevision?> FindAsync(int dialogueId, int revisionId, CancellationToken ct) =>
        db.DialogueRevisions.AsNoTracking().SingleOrDefaultAsync(
            revision => revision.DialogueId == dialogueId && revision.Id == revisionId, ct);

    private static DialogueDocument ReadDocument(DialogueRevision revision) =>
        JsonSerializer.Deserialize<DialogueDocument>(revision.DocumentJson, JsonSerializerOptions.Web)
        ?? throw new InvalidDataException("Сохранённая версия диалога не содержит документа.");
}
