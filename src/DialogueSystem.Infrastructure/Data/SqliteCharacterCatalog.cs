using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteCharacterCatalog(DialogueDbContext db) : ICharacterCatalog
{
    public Task<CatalogPage<CharacterSummary>> PageAsync(string? query, int? before, int? except, CancellationToken ct)
    {
        var selected = db.Characters.AsQueryable();
        if (before.HasValue) selected = selected.Where(character => character.Id < before.Value);
        if (except.HasValue) selected = selected.Where(character => character.Id != except.Value);
        var search = query?.Trim();
        if (!string.IsNullOrEmpty(search)) selected = selected.Where(character => SqliteSearch.Contains(character.Name, search));
        return CatalogQueries.PageAsync(selected.OrderByDescending(character => character.Id)
            .Select(character => new CharacterSummary(character.Id, character.Key, character.Name, character.Revision)),
            character => character.Id, ct);
    }

    public Task<CharacterSummary?> FindAsync(int id, CancellationToken ct) =>
        db.Characters.Where(character => character.Id == id)
            .Select(character => new CharacterSummary(character.Id, character.Key, character.Name, character.Revision)).SingleOrDefaultAsync(ct);

    public async Task<CharacterSummary> CreateAsync(string name, CancellationToken ct)
    {
        var character = new Character { Name = name.Trim() };
        db.Characters.Add(character);
        await db.SaveChangesAsync(ct);
        return new(character.Id, character.Key, character.Name, character.Revision);
    }

    public async Task<ChangeStatus> RenameAsync(int id, string name, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return ChangeStatus.NotFound;
        if (version != current.Version) return ChangeStatus.Conflict;
        var normalized = name.Trim();
        await db.Characters.Where(character => character.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(character => character.Name, normalized), ct);
        await transaction.CommitAsync(ct);
        return ChangeStatus.Saved;
    }

    public Task<CharacterUsage?> UsageAsync(int id, CancellationToken ct, int? before = null) =>
        SqliteReadSnapshot.ReadAsync(db, () => ReadUsageAsync(id, before, ct), ct);

    private async Task<CharacterUsage?> ReadUsageAsync(int id, int? before, CancellationToken ct)
    {
        var character = await FindAsync(id, ct);
        if (character is null)
            return null;

        var counts = db.Lines.Where(line => line.SpeakerId == id).GroupBy(line => line.DialogueId)
            .Select(group => new
            {
                DialogueId = group.Key,
                FirstLineId = group.Min(line => line.Id),
                LineCount = group.Count()
            });
        var totalLines = await db.Lines.CountAsync(line => line.SpeakerId == id, ct);
        var totalDialogues = await counts.CountAsync(ct);
        var selected =
            from count in counts
            join dialogue in db.Dialogues on count.DialogueId equals dialogue.Id
            where before == null || dialogue.Id < before.Value
            orderby dialogue.Id descending
            select new CharacterDialogueUsage(dialogue.Id, dialogue.Title, count.FirstLineId, count.LineCount);
        var page = await CatalogQueries.PageAsync(selected, dialogue => dialogue.DialogueId, ct);
        return new(character.Id, character.Name, totalLines, page.Items)
        {
            Version = character.Version, DialogueCount = totalDialogues, NextBeforeId = page.NextBeforeId
        };
    }

    public async Task<CharacterRemoval> DeleteAsync(int id, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return CharacterRemoval.NotFound;
        if (version != current.Version) return CharacterRemoval.Conflict;
        var selected = db.Characters.Where(character => character.Id == id);
        if (await selected.Where(character => !db.Lines.Any(line => line.SpeakerId == character.Id))
                .ExecuteDeleteAsync(ct) == 0)
        {
            return await selected.AnyAsync(ct) ? CharacterRemoval.InUse : CharacterRemoval.NotFound;
        }

        await transaction.CommitAsync(ct);
        return CharacterRemoval.Removed;
    }

    public async Task<CharacterReassignment> ReassignAsync(
        int sourceId, int targetId, int? dialogueId, string version, string targetVersion, CancellationToken ct)
    {
        if (sourceId == targetId)
            return new(ReassignmentStatus.SameCharacter);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var characters = await db.Characters.Where(character => character.Id == sourceId || character.Id == targetId)
            .Select(character => new CharacterSummary(character.Id, character.Key, character.Name, character.Revision)).ToListAsync(ct);
        var source = characters.SingleOrDefault(character => character.Id == sourceId);
        var target = characters.SingleOrDefault(character => character.Id == targetId);
        if (source is null)
            return new(ReassignmentStatus.SourceNotFound);
        if (target is null)
            return new(ReassignmentStatus.TargetNotFound);
        if (version != source.Version || targetVersion != target.Version)
            return new(ReassignmentStatus.Conflict);

        var lines = db.Lines.Where(line => line.SpeakerId == sourceId);
        if (dialogueId is int scope)
        {
            if (!await db.Dialogues.AnyAsync(dialogue => dialogue.Id == scope, ct))
                return new(ReassignmentStatus.DialogueNotFound);
            lines = lines.Where(line => line.DialogueId == scope);
        }

        var updated = await lines.ExecuteUpdateAsync(setters => setters.SetProperty(line => line.SpeakerId, targetId), ct);
        await transaction.CommitAsync(ct);
        return new(ReassignmentStatus.Reassigned, updated);
    }
}
