using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteDialogueCatalog(DialogueDbContext db, GraphSnapshots cache) : IDialogueCatalog
{
    public async Task<IReadOnlyList<DialogueSummary>> ListAsync(CancellationToken ct) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title).ThenBy(dialogue => dialogue.Id).Take(CatalogLimits.LegacyListSize)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel, dialogue.Revision, dialogue.ActivityRevision)).ToListAsync(ct);

    public Task<DialogueSummary?> FindAsync(int id, CancellationToken ct) =>
        db.Dialogues.Where(dialogue => dialogue.Id == id)
            .Select(dialogue => new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId,
                dialogue.MinimumPlayerLevel, dialogue.Revision, dialogue.ActivityRevision)).SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<DialogueOverview>> OverviewAsync(CancellationToken ct) =>
        await db.Dialogues.OrderBy(dialogue => dialogue.Title).ThenBy(dialogue => dialogue.Id).Take(CatalogLimits.LegacyListSize)
            .Select(dialogue => new DialogueOverview(dialogue.Id, dialogue.Title, dialogue.StartLineId != null,
                dialogue.MinimumPlayerLevel, dialogue.Lines.Count,
                dialogue.Lines.Count(line => line.IsStoryBranch), dialogue.Lines.Count(line => line.IsEnding)))
            .ToListAsync(ct);

    public async Task<LineResource?> FindLineAsync(int id, int lineId, CancellationToken ct)
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

    public async Task<ChangeStatus> RenameAsync(int id, string title, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return ChangeStatus.NotFound;
        if (version != current.Version) return ChangeStatus.Conflict;
        var normalized = title.Trim();
        await db.Dialogues.Where(dialogue => dialogue.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.Title, normalized), ct);
        await transaction.CommitAsync(ct);
        return ChangeStatus.Saved;
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

    public async Task<ChangeStatus> SetMinimumLevelAsync(int id, int? minimum, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var current = await FindAsync(id, ct);
        if (current is null) return ChangeStatus.NotFound;
        if (version != current.Version) return ChangeStatus.Conflict;
        await db.Dialogues.Where(dialogue => dialogue.Id == id).ExecuteUpdateAsync(
            setters => setters.SetProperty(dialogue => dialogue.MinimumPlayerLevel, minimum), ct);
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

    public Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null) =>
        SqliteReadSnapshot.ReadAsync(db, () => ReadContextAsync(id, lineId, outgoingAfter, incomingAfterLine, incomingAfterPosition, ct), ct);

    private async Task<LineContext?> ReadContextAsync(int id, int lineId, int? outgoingAfter,
        int? incomingAfterLine, int? incomingAfterPosition, CancellationToken ct)
    {
        var dialogue = await FindAsync(id, ct);
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

    public async Task<IReadOnlyList<LineSummary>?> BranchesAsync(int id, CancellationToken ct)
    {
        var lines = await db.Lines.Where(line => line.DialogueId == id && line.IsStoryBranch)
            .OrderBy(line => line.Id).Take(CatalogLimits.LegacyListSize).Select(line => new LineSummary(line.Id, line.Speaker.Name, line.Text,
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
    public async Task<IReadOnlyList<CharacterSummary>> ListAsync(CancellationToken ct) =>
        await db.Characters.OrderBy(character => character.Name).ThenBy(character => character.Id).Take(CatalogLimits.LegacyListSize)
            .Select(character => new CharacterSummary(character.Id, character.Key, character.Name, character.Revision)).ToListAsync(ct);

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
