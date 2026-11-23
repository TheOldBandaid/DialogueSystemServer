using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteDialogueCatalog(DialogueDbContext db) : IDialogueCatalog
{
    public async Task<IReadOnlyList<DialogueSummary>> ListAsync(CancellationToken ct) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel)).ToListAsync(ct);

    public Task<DialogueSummary?> FindAsync(int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel)).SingleOrDefaultAsync(ct);

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

    public Task<IReadOnlyList<LineSummary>?> BranchesAsync(int id, CancellationToken ct) =>
        ReadGraphAsync<IReadOnlyList<LineSummary>>(id, graph => graph.GetStoryBranches(), ct);

    public Task<DialogueValidation?> ValidateAsync(int id, CancellationToken ct) =>
        ReadGraphAsync(id, graph => graph.Validate(), ct);

    private async Task<T?> ReadGraphAsync<T>(int id, Func<DialogueGraph, T> select, CancellationToken ct)
        where T : class
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var graph = await DialogueGraphLoader.LoadAsync(db, id, ct);
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
}
