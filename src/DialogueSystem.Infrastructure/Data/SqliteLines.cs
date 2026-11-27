using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteLines(DialogueDbContext db) : ILineStore
{
    public async Task<LineResource?> ReadLineAsync(int id, int lineId, CancellationToken ct)
    {
        var resource = await (from line in db.Lines
            join dialogue in db.Dialogues on line.DialogueId equals dialogue.Id
            where dialogue.Id == id && line.Id == lineId
            select new
            {
                Line = new LineSummary(line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId),
                dialogue.Revision
            }).SingleOrDefaultAsync(ct);
        return resource is null ? null : new(resource.Line,
            EditVersions.LineDeletion(lineId, resource.Line.Version, resource.Revision));
    }

    public Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null) =>
        SqliteReadSnapshot.ReadAsync(db, () => ReadContextAsync(id, lineId, outgoingAfter, incomingAfterLine, incomingAfterPosition, ct), ct);

    private async Task<LineContext?> ReadContextAsync(int id, int lineId, int? outgoingAfter,
        int? incomingAfterLine, int? incomingAfterPosition, CancellationToken ct)
    {
        var dialogue = await CatalogQueries.FindDialogueAsync(db, id, ct);
        if (dialogue is null) return null;
        var line = await db.Lines.Where(line => line.DialogueId == id && line.Id == lineId)
            .Select(line => new LineSummary(line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId))
            .SingleOrDefaultAsync(ct);
        if (line is null) return null;
        var neighbors =
            from choice in db.Choices
            where choice.DialogueId == id && (choice.SourceLineId == lineId || choice.TargetLineId == lineId)
            let neighborId = choice.SourceLineId == lineId ? choice.TargetLineId : choice.SourceLineId
            join neighbor in db.Lines
                on new { choice.DialogueId, Id = neighborId } equals new { neighbor.DialogueId, neighbor.Id }
            select new
            {
                choice.DialogueId,
                choice.SourceLineId,
                choice.TargetLineId,
                choice.Position,
                Link = new ChoiceLink(choice.Id, choice.Text, choice.Position,
                    new LineSummary(neighbor.Id, neighbor.Speaker.Name, neighbor.Text,
                        neighbor.IsEnding, neighbor.IsStoryBranch, neighbor.SpeakerId),
                    choice.RequiredState, choice.NextState, choice.TargetLineId)
            };

        var incoming = await neighbors.Where(choice => choice.TargetLineId == lineId &&
                (incomingAfterLine == null || choice.SourceLineId > incomingAfterLine ||
                 choice.SourceLineId == incomingAfterLine && choice.Position > incomingAfterPosition))
            .OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position)
            .Select(choice => choice.Link).Take(CatalogLimits.PageSize + 1).ToListAsync(ct);
        var outgoing = await neighbors.Where(choice => choice.SourceLineId == lineId &&
                (outgoingAfter == null || choice.Position > outgoingAfter))
            .OrderBy(choice => choice.Position).Select(choice => choice.Link)
            .Take(CatalogLimits.PageSize + 1).ToListAsync(ct);
        var nextIncoming = incoming.Count > CatalogLimits.PageSize
            ? new IncomingChoiceCursor(incoming[CatalogLimits.PageSize - 1].Line.Id, incoming[CatalogLimits.PageSize - 1].Position) : null;
        var nextOutgoing = outgoing.Count > CatalogLimits.PageSize ? outgoing[CatalogLimits.PageSize - 1].Position : (int?)null;
        if (nextIncoming is not null) incoming.RemoveAt(CatalogLimits.PageSize);
        if (nextOutgoing.HasValue) outgoing.RemoveAt(CatalogLimits.PageSize);
        var positions = db.Choices.Where(choice => choice.DialogueId == id && choice.SourceLineId == lineId).Select(choice => choice.Position);
        var maximum = await positions.Select(position => (int?)position).MaxAsync(ct) ?? 0;
        var suggested = maximum < int.MaxValue ? maximum + 1 : 1;
        if (maximum == int.MaxValue)
        {
            foreach (var used in await positions.Order().ToListAsync(ct))
            {
                if (used > suggested) break;
                if (used == suggested) suggested++;
            }
        }
        return new(line, incoming, outgoing)
        {
            Dialogue = dialogue, DeleteVersion = EditVersions.LineDeletion(lineId, line.Version, dialogue.Revision),
            IncomingCount = await db.Choices.CountAsync(choice => choice.DialogueId == id && choice.TargetLineId == lineId, ct),
            OutgoingCount = await db.Choices.CountAsync(choice => choice.DialogueId == id && choice.SourceLineId == lineId, ct),
            NextIncoming = nextIncoming, NextOutgoingPosition = nextOutgoing, SuggestedChoicePosition = suggested
        };
    }

    public async Task<IStorageTransaction> BeginAsync(CancellationToken ct) =>
        new SqliteTransaction(await db.Database.BeginTransactionAsync(ct));

    public Task<bool> DialogueExistsAsync(int id, CancellationToken ct) =>
        db.Dialogues.AnyAsync(dialogue => dialogue.Id == id, ct);

    public Task<int> CountLinesAsync(int id, CancellationToken ct) =>
        db.Lines.CountAsync(line => line.DialogueId == id, ct);

    public Task<int> CountChoicesAsync(int id, CancellationToken ct) =>
        db.Choices.CountAsync(choice => choice.DialogueId == id, ct);

    public Task<long?> DialogueRevisionAsync(int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id).Select(dialogue => (long?)dialogue.Revision).SingleOrDefaultAsync(ct);

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
