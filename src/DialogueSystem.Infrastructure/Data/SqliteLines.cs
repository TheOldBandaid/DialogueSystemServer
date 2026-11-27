using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteLines(DialogueDbContext db) : ILineStore
{
    public async Task<IStorageTransaction> BeginAsync(CancellationToken ct) =>
        new SqliteTransaction(await db.Database.BeginTransactionAsync(ct));

    public Task<bool> DialogueExistsAsync(int id, CancellationToken ct) =>
        db.Dialogues.AnyAsync(dialogue => dialogue.Id == id, ct);

    public Task<int> CountLinesAsync(int id, CancellationToken ct) =>
        db.Lines.CountAsync(line => line.DialogueId == id, ct);

    public Task<int> CountChoicesAsync(int id, CancellationToken ct) =>
        db.Choices.CountAsync(choice => choice.DialogueId == id, ct);

    public async Task<Dialogue?> FindDialogueAsync(int id, CancellationToken ct) =>
        await db.Dialogues.FindAsync([id], ct);

    public Task<long?> DialogueRevisionAsync(int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id).Select(dialogue => (long?)dialogue.Revision).SingleOrDefaultAsync(ct);

    public Task<List<LineSummary>> ListAsync(int id, CancellationToken ct) =>
        db.Lines.Where(line => line.DialogueId == id).OrderBy(line => line.Id).Take(CatalogLimits.LegacyListSize)
            .Select(line => new LineSummary(line.Id, line.Speaker.Name, line.Text, line.IsEnding,
                line.IsStoryBranch, line.SpeakerId))
            .ToListAsync(ct);

    public async Task<CatalogPage<LineReference>?> PageAsync(int id, string? query, int? before, bool branchesOnly, CancellationToken ct)
    {
        var selected = db.Lines.Where(line => line.DialogueId == id);
        if (before.HasValue) selected = selected.Where(line => line.Id < before.Value);
        if (branchesOnly) selected = selected.Where(line => line.IsStoryBranch);
        var search = query?.Trim();
        if (!string.IsNullOrEmpty(search))
            selected = selected.Where(line => SqliteSearch.Contains(line.Text, search) || SqliteSearch.Contains(line.Speaker.Name, search));
        var page = await CatalogQueries.PageAsync(selected.OrderByDescending(line => line.Id)
            .Select(line => new LineReference(line.Id, line.Speaker.Name,
                line.Text.Length > 240 ? line.Text.Substring(0, 240) + "…" : line.Text,
                line.IsEnding, line.IsStoryBranch, line.SpeakerId)), line => line.Id, ct);
        return page.Items.Count == 0 && !await DialogueExistsAsync(id, ct) ? null : page;
    }

    public Task<DialogueLine?> FindLineAsync(int id, int lineId, CancellationToken ct) =>
        db.Lines.SingleOrDefaultAsync(line => line.DialogueId == id && line.Id == lineId, ct);

    public Task<bool> LineExistsAsync(int id, int lineId, CancellationToken ct) =>
        db.Lines.AnyAsync(line => line.DialogueId == id && line.Id == lineId, ct);

    public async Task<Character?> FindCharacterAsync(int id, CancellationToken ct) =>
        await db.Characters.FindAsync([id], ct);

    public Task<bool> IsStartAsync(int id, int lineId, CancellationToken ct) =>
        db.Dialogues.AnyAsync(dialogue => dialogue.Id == id && dialogue.StartLineId == lineId, ct);

    public Task<bool> HasIncomingAsync(int id, int lineId, CancellationToken ct) =>
        db.Choices.AnyAsync(choice => choice.DialogueId == id && choice.TargetLineId == lineId &&
            choice.SourceLineId != lineId, ct);

    public Task<bool> HasOutgoingAsync(int id, int lineId, CancellationToken ct) =>
        db.Choices.AnyAsync(choice => choice.DialogueId == id && choice.SourceLineId == lineId, ct);

    public Task<bool> PositionTakenAsync(int id, int lineId, int position, int exceptChoice, CancellationToken ct) =>
        db.Choices.AnyAsync(choice => choice.DialogueId == id && choice.SourceLineId == lineId &&
            choice.Position == position && choice.Id != exceptChoice, ct);

    public Task<DialogueChoice?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct) =>
        db.Choices.SingleOrDefaultAsync(choice => choice.DialogueId == id &&
            choice.SourceLineId == lineId && choice.Id == choiceId, ct);

    public Task<DialogueChoice?> NeighborChoiceAsync(int id, int lineId, int position, int direction, CancellationToken ct)
    {
        var choices = db.Choices.Where(choice => choice.DialogueId == id && choice.SourceLineId == lineId);
        return direction < 0
            ? choices.Where(choice => choice.Position < position).OrderByDescending(choice => choice.Position).FirstOrDefaultAsync(ct)
            : choices.Where(choice => choice.Position > position).OrderBy(choice => choice.Position).FirstOrDefaultAsync(ct);
    }

    public Task SwapChoicesAsync(DialogueChoice first, DialogueChoice second, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE Choices SET Position = 0 WHERE Id = {first.Id};
            UPDATE Choices SET Position = {first.Position} WHERE Id = {second.Id};
            UPDATE Choices SET Position = {second.Position} WHERE Id = {first.Id};
            """, ct);

    public Task DeleteOutgoingAsync(int id, int lineId, CancellationToken ct) =>
        db.Choices.Where(choice => choice.DialogueId == id && choice.SourceLineId == lineId).ExecuteDeleteAsync(ct);

    public async Task<bool> DeleteChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var deleted = await db.Choices.Where(choice => choice.DialogueId == id &&
            choice.SourceLineId == lineId && choice.Id == choiceId).ExecuteDeleteAsync(ct);
        return deleted != 0;
    }

    public void AddLine(DialogueLine line) => db.Lines.Add(line);
    public void RemoveLine(DialogueLine line) => db.Lines.Remove(line);
    public void AddChoice(DialogueChoice choice) => db.Choices.Add(choice);
    public Task SaveAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}
