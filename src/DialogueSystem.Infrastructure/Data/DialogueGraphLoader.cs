using DialogueSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal static class DialogueGraphLoader
{
    public static async Task<DialogueGraph?> LoadAsync(
        DialogueDbContext db, GraphSnapshots cache, int dialogueId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is not null)
            return await LoadSnapshotAsync(db, cache, dialogueId, cancellationToken, publish: false);

        return await SqliteReadSnapshot.ReadAsync(db,
            () => LoadSnapshotAsync(db, cache, dialogueId, cancellationToken, publish: true), cancellationToken);
    }

    private static async Task<DialogueGraph?> LoadSnapshotAsync(
        DialogueDbContext db, GraphSnapshots cache, int dialogueId, CancellationToken cancellationToken, bool publish)
    {
        var revision = await db.Dialogues.Where(dialogue => dialogue.Id == dialogueId)
            .Select(dialogue => (long?)dialogue.Revision).SingleOrDefaultAsync(cancellationToken);
        if (revision is null) return null;
        if (cache.TryGet(dialogueId, revision.Value, out var cached)) return cached;
        var dialogue = await db.Dialogues.AsNoTracking()
            .Include(dialogue => dialogue.Lines).ThenInclude(line => line.Speaker)
            .SingleOrDefaultAsync(dialogue => dialogue.Id == dialogueId, cancellationToken);
        if (dialogue is null)
        {
            return null;
        }

        var choices = await db.Choices.AsNoTracking().Where(choice => choice.DialogueId == dialogueId)
            .ToListAsync(cancellationToken);
        var graph = new DialogueGraph(dialogue, choices);
        if (publish)
        {
            var textSize = 2 * (dialogue.Lines.Sum(line => (long)line.Text.Length + line.Speaker.Name.Length) +
                choices.Sum(choice => (long)choice.Text.Length + (choice.RequiredState?.Length ?? 0) + (choice.NextState?.Length ?? 0)));
            var states = choices.Any(choice => choice.RequiredState is not null)
                ? choices.Select(choice => choice.NextState).Append(null).Distinct(StringComparer.Ordinal).Count()
                : 1;
            var positions = Math.Min(DialogueLimits.MaximumPositions, (long)dialogue.Lines.Count * states);
            var estimatedSize = 1024L + textSize + 256L * (dialogue.Lines.Count + choices.Count) + 160L * positions;
            cache.Set(dialogueId, revision.Value, graph, estimatedSize);
        }
        return graph;
    }
}
