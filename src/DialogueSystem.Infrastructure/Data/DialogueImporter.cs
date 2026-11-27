using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal static class DialogueImporter
{
    public static async Task<DialogueSummary> SaveAsync(
        DialogueDbContext db, DialogueDocument document, CancellationToken cancellationToken, bool cloneCharacters = false)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await SaveWithinTransactionAsync(db, document, cancellationToken, cloneCharacters);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<DialogueSummary> SaveWithinTransactionAsync(
        DialogueDbContext db, DialogueDocument document, CancellationToken cancellationToken, bool cloneCharacters = false)
    {
        string SpeakerKey(DialogueDocumentLine line) => document.Version == 2 ? line.SpeakerKey! : line.Speaker.Trim();
        var requested = document.Lines.GroupBy(SpeakerKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Speaker.Trim(), StringComparer.Ordinal);
        var keys = requested.Keys.ToArray();
        var existing = cloneCharacters ? [] : document.Version == 2
            ? await db.Characters.Where(character => keys.Contains(character.Key) ||
                character.OriginKey != null && keys.Contains(character.OriginKey)).OrderBy(character => character.Id)
                .ToListAsync(cancellationToken)
            : await db.Characters.Where(character => keys.Contains(character.Name)).OrderBy(character => character.Id)
                .ToListAsync(cancellationToken);
        var speakers = new Dictionary<string, Character>(StringComparer.Ordinal);
        var byKey = existing.ToDictionary(character => character.Key, StringComparer.Ordinal);
        var byName = existing.GroupBy(character => character.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var variants = existing.Where(character => character.OriginKey is not null)
            .GroupBy(character => (character.OriginKey!, character.Name))
            .ToDictionary(group => group.Key, group => group.First());
        foreach (var (key, name) in requested)
        {
            byKey.TryGetValue(key, out var original);
            var character = document.Version == 2
                ? original?.Name == name ? original : variants.GetValueOrDefault((key, name))
                : byName.GetValueOrDefault(name);
            if (character is not null) { speakers[key] = character; continue; }
            var renamed = document.Version == 2 && original is not null;
            // Reuse a snapshot variant on subsequent imports; never merge unrelated namesakes.
            speakers[key] = new Character
            {
                Name = name,
                Key = !cloneCharacters && document.Version == 2 && !renamed ? key : Guid.NewGuid().ToString("N"),
                OriginKey = !cloneCharacters && renamed ? key : null
            };
        }

        var dialogue = new Dialogue
        {
            Title = document.Title.Trim(),
            MinimumPlayerLevel = document.MinimumPlayerLevel
        };
        var lines = document.Lines.ToDictionary(line => line.Id, line => new DialogueLine
        {
            Speaker = speakers[SpeakerKey(line)],
            Text = line.Text.Trim(),
            IsEnding = line.IsEnding,
            IsStoryBranch = line.IsStoryBranch
        });
        dialogue.Lines.AddRange(lines.Values);
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync(cancellationToken);

        dialogue.StartLineId = document.StartLineId is int startId ? lines[startId].Id : null;
        db.Choices.AddRange(document.Choices.Select(choice => new DialogueChoice
        {
            DialogueId = dialogue.Id,
            SourceLineId = lines[choice.SourceLineId].Id,
            TargetLineId = lines[choice.TargetLineId].Id,
            Text = choice.Text.Trim(),
            Position = choice.Position,
            RequiredState = DialogueChoice.NormalizeStoryState(choice.RequiredState),
            NextState = DialogueChoice.NormalizeStoryState(choice.NextState)
        }));
        await db.SaveChangesAsync(cancellationToken);
        await db.Entry(dialogue).ReloadAsync(cancellationToken);
        return new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId, dialogue.MinimumPlayerLevel, dialogue.Revision, dialogue.ActivityRevision);
    }
}
