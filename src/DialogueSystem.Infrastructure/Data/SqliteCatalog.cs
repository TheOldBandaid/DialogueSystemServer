using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteDialogueCatalog(DialogueDbContext db, GraphSnapshots cache) : IDialogueCatalog
{
    public async Task<IReadOnlyList<DialogueSummary>> ListAsync(CancellationToken ct) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel)).ToListAsync(ct);

    public Task<DialogueSummary?> FindAsync(int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DialogueOverview>> OverviewAsync(CancellationToken ct) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title).ThenBy(dialogue => dialogue.Id)
            .Select(dialogue => new DialogueOverview(dialogue.Id, dialogue.Title, dialogue.StartLineId != null,
                dialogue.MinimumPlayerLevel, dialogue.Lines.Count,
                dialogue.Lines.Count(line => line.IsStoryBranch), dialogue.Lines.Count(line => line.IsEnding)))
            .ToListAsync(ct);

    public async Task<DialogueSummary> CreateAsync(string title, CancellationToken ct)
    {
        var dialogue = new Dialogue { Title = title.Trim() };
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync(ct);
        return new(dialogue.Id, dialogue.Title, dialogue.StartLineId, dialogue.MinimumPlayerLevel);
    }

    public async Task<bool> RenameAsync(int id, string title, CancellationToken ct)
    {
        var normalized = title.Trim();
        var updated = await db.Dialogues.Where(dialogue => dialogue.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.Title, normalized), ct);
        return updated != 0;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var selected = db.Dialogues.Where(dialogue => dialogue.Id == id);
        var updated = await selected.ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.StartLineId, (int?)null), ct);
        if (updated == 0)
            return false;

        await db.Choices.Where(choice => choice.DialogueId == id).ExecuteDeleteAsync(ct);
        await selected.ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> SetMinimumLevelAsync(int id, int? minimum, CancellationToken ct)
    {
        var updated = await db.Dialogues.Where(dialogue => dialogue.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.MinimumPlayerLevel, minimum), ct);
        return updated != 0;
    }

    public Task<DialogueDocument?> ExportAsync(int id, CancellationToken ct) =>
        ReadGraphAsync(id, graph => graph.ToDocument(), ct);

    public Task<DialogueSummary> ImportAsync(DialogueDocument document, CancellationToken ct) =>
        DialogueImporter.SaveAsync(db, document, ct);

    public async Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct)
    {
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
                Link = new ChoiceLink(choice.Id, choice.Text, choice.Position,
                    new LineSummary(neighbor.Id, neighbor.Speaker.Name, neighbor.Text,
                        neighbor.IsEnding, neighbor.IsStoryBranch, neighbor.SpeakerId),
                    choice.RequiredState, choice.NextState)
            };

        var context = await db.Lines.AsSingleQuery()
            .Where(line => line.DialogueId == id && line.Id == lineId)
            .Select(line => new
            {
                Line = new LineSummary(line.Id, line.Speaker.Name, line.Text, line.IsEnding,
                    line.IsStoryBranch, line.SpeakerId),
                Neighbors = neighbors.Where(item => item.DialogueId == line.DialogueId).ToList()
            })
            .SingleOrDefaultAsync(ct);
        if (context is null)
            return null;

        var incoming = context.Neighbors.Where(choice => choice.TargetLineId == lineId)
            .OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Link.Position)
            .Select(choice => choice.Link).ToArray();
        var outgoing = context.Neighbors.Where(choice => choice.SourceLineId == lineId)
            .OrderBy(choice => choice.Link.Position).Select(choice => choice.Link).ToArray();
        return new(context.Line, incoming, outgoing);
    }

    public async Task<IReadOnlyList<LineSummary>?> BranchesAsync(int id, CancellationToken ct)
    {
        var lines = await db.Lines.Where(line => line.DialogueId == id && line.IsStoryBranch)
            .OrderBy(line => line.Id).Select(line => new LineSummary(line.Id, line.Speaker.Name, line.Text,
                line.IsEnding, line.IsStoryBranch, line.SpeakerId)).ToListAsync(ct);
        return lines.Count == 0 && !await db.Dialogues.AnyAsync(dialogue => dialogue.Id == id, ct) ? null : lines;
    }

    public Task<DialogueValidation?> ValidateAsync(int id, CancellationToken ct) =>
        ReadGraphAsync(id, graph => graph.Validate(ct: ct), ct);

    private async Task<T?> ReadGraphAsync<T>(int id, Func<DialogueGraph, T> select, CancellationToken ct)
        where T : class
    {
        var graph = await DialogueGraphLoader.LoadAsync(db, cache, id, ct);
        return graph is null ? null : select(graph);
    }
}

internal sealed class SqliteCharacterCatalog(DialogueDbContext db) : ICharacterCatalog
{
    public async Task<IReadOnlyList<Character>> ListAsync(CancellationToken ct) =>
        await db.Characters.AsNoTracking().OrderBy(character => character.Name).ToListAsync(ct);

    public Task<Character?> FindAsync(int id, CancellationToken ct) =>
        db.Characters.AsNoTracking().SingleOrDefaultAsync(character => character.Id == id, ct);

    public async Task<Character> CreateAsync(string name, CancellationToken ct)
    {
        var character = new Character { Name = name.Trim() };
        db.Characters.Add(character);
        await db.SaveChangesAsync(ct);
        return character;
    }

    public async Task<bool> RenameAsync(int id, string name, CancellationToken ct)
    {
        var normalized = name.Trim();
        var updated = await db.Characters.Where(character => character.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(character => character.Name, normalized), ct);
        return updated != 0;
    }

    public async Task<CharacterUsage?> UsageAsync(int id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
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
        var dialogues = await (
            from count in counts
            join dialogue in db.Dialogues on count.DialogueId equals dialogue.Id
            orderby dialogue.Title, dialogue.Id
            select new CharacterDialogueUsage(dialogue.Id, dialogue.Title, count.FirstLineId, count.LineCount))
            .ToListAsync(ct);

        return new(character.Id, character.Name, dialogues.Sum(dialogue => dialogue.LineCount), dialogues);
    }

    public async Task<CharacterRemoval> DeleteAsync(int id, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
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
        int sourceId, int targetId, int? dialogueId, CancellationToken ct)
    {
        if (sourceId == targetId)
            return new(ReassignmentStatus.SameCharacter);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var characters = await db.Characters.Where(character => character.Id == sourceId || character.Id == targetId)
            .Select(character => character.Id).ToListAsync(ct);
        if (!characters.Contains(sourceId))
            return new(ReassignmentStatus.SourceNotFound);
        if (!characters.Contains(targetId))
            return new(ReassignmentStatus.TargetNotFound);

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
