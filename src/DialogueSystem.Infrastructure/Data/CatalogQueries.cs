using DialogueSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal static class CatalogQueries
{
    public static Task<DialogueSummary?> FindDialogueAsync(DialogueDbContext db, int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel, dialogue.Revision, dialogue.ActivityRevision)).SingleOrDefaultAsync(ct);

    public static async Task<CatalogPage<T>> PageAsync<T>(IQueryable<T> query, Func<T, int> id, CancellationToken ct)
    {
        var rows = await query.Take(CatalogLimits.PageSize + 1).ToListAsync(ct);
        var next = rows.Count > CatalogLimits.PageSize ? id(rows[CatalogLimits.PageSize - 1]) : (int?)null;
        if (next.HasValue) rows.RemoveAt(CatalogLimits.PageSize);
        return new(rows, next);
    }
}
