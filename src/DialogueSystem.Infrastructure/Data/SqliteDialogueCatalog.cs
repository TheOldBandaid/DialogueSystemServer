using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteDialogueCatalog(DialogueDbContext db, GraphSnapshots cache) : IDialogueCatalog
{
    public Task<DialogueSummary?> FindAsync(int id, CancellationToken ct) =>
        CatalogQueries.FindDialogueAsync(db, id, ct);

    public Task<CatalogPage<DialogueOverview>> PageAsync(string? query, bool? hasStart, int? before, CancellationToken ct)
    {
        var selected = db.Dialogues.AsQueryable();
        if (before.HasValue) selected = selected.Where(dialogue => dialogue.Id < before.Value);
        if (hasStart.HasValue) selected = selected.Where(dialogue => (dialogue.StartLineId != null) == hasStart.Value);
        var search = query?.Trim();
        if (!string.IsNullOrEmpty(search)) selected = selected.Where(dialogue => SqliteSearch.Contains(dialogue.Title, search));
        return CatalogQueries.PageAsync(selected.OrderByDescending(dialogue => dialogue.Id)
            .Select(dialogue => new DialogueOverview(dialogue.Id, dialogue.Title, dialogue.StartLineId != null,
                dialogue.MinimumPlayerLevel, dialogue.Lines.Count, dialogue.Lines.Count(line => line.IsStoryBranch),
                dialogue.Lines.Count(line => line.IsEnding))), dialogue => dialogue.Id, ct);
    }

    public async Task<DialogueSummary> CreateAsync(string title, CancellationToken ct)
    {
        var dialogue = new Dialogue { Title = title.Trim() };
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync(ct);
        return new(dialogue.Id, dialogue.Title, dialogue.StartLineId, dialogue.MinimumPlayerLevel);
    }

    public async Task<EditResult> UpdateAsync(int id, DialogueChange input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return new(EditStatus.NotFound);
        if (input.Version != current.Version)
            return new(EditStatus.Conflict, "Диалог уже изменился. Обновите настройки перед сохранением.");
        if (input.StartLineId is int start && !await db.Lines.AnyAsync(line => line.DialogueId == id && line.Id == start, ct))
            return new(EditStatus.Invalid, "Стартовая реплика должна принадлежать этому диалогу.");
        var normalized = input.Title.Trim();
        await db.Dialogues.Where(dialogue => dialogue.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.Title, normalized)
                .SetProperty(dialogue => dialogue.StartLineId, input.StartLineId)
                .SetProperty(dialogue => dialogue.MinimumPlayerLevel, input.MinimumPlayerLevel), ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<ChangeStatus> DeleteAsync(int id, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return ChangeStatus.NotFound;
        if (version != current.DeleteVersion) return ChangeStatus.Conflict;
        var selected = db.Dialogues.Where(dialogue => dialogue.Id == id);
        await selected.ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.StartLineId, (int?)null), ct);

        await db.Choices.Where(choice => choice.DialogueId == id).ExecuteDeleteAsync(ct);
        await selected.ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
        return ChangeStatus.Saved;
    }

    public Task<DialogueDocument?> ExportAsync(int id, CancellationToken ct) =>
        ReadGraphAsync(id, graph => graph.ToDocument(), ct);

    public Task<DialogueMapSnapshot?> MapAsync(int id, CancellationToken ct) =>
        SqliteReadSnapshot.ReadAsync(db, () => ReadMapAsync(id, ct), ct);

    private async Task<DialogueMapSnapshot?> ReadMapAsync(int id, CancellationToken ct)
    {
        var dialogue = await FindAsync(id, ct);
        if (dialogue is null) return null;
        var lines = await db.Lines.Where(line => line.DialogueId == id).OrderBy(line => line.Id)
            .Select(line => new DialogueMapLine(line.Id, line.Speaker.Name,
                line.Text.Length > 240 ? line.Text.Substring(0, 240) + "…" : line.Text, line.IsEnding, line.IsStoryBranch))
            .Take(DialogueLimits.MaximumLines + 1).ToListAsync(ct);
        var choices = await db.Choices.Where(choice => choice.DialogueId == id)
            .OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position)
            .Select(choice => new DialogueMapChoice(choice.SourceLineId, choice.TargetLineId,
                choice.Text.Length > 120 ? choice.Text.Substring(0, 120) + "…" : choice.Text,
                choice.Position, choice.RequiredState, choice.NextState))
            .Take(DialogueLimits.MaximumChoices + 1).ToListAsync(ct);
        return lines.Count > DialogueLimits.MaximumLines || choices.Count > DialogueLimits.MaximumChoices
            ? new(id, dialogue.Revision, dialogue.StartLineId, [], [], false)
            : new(id, dialogue.Revision, dialogue.StartLineId, lines, choices);
    }

    public Task<DialogueSummary> ImportAsync(DialogueDocument document, CancellationToken ct) =>
        DialogueImporter.SaveAsync(db, document, ct);

    public async Task<DialogueSummary?> CopyAsync(int id, bool cloneCharacters, CancellationToken ct)
    {
        var document = await ExportAsync(id, ct);
        if (document is null) return null;
        var suffix = " (копия)";
        var title = document.Title.Length + suffix.Length <= 160 ? document.Title + suffix : "Копия диалога";
        return await DialogueImporter.SaveAsync(db, document with { Title = title }, ct, cloneCharacters);
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
