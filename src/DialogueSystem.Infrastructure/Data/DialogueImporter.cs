using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal static class DialogueImporter
{
    public static async Task<DialogueSummary> SaveAsync(
        DialogueDbContext db, DialogueDocument document, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var result = await SaveWithinTransactionAsync(db, document, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    internal static async Task<DialogueSummary> SaveWithinTransactionAsync(
        DialogueDbContext db, DialogueDocument document, CancellationToken cancellationToken)
    {
        string SpeakerKey(DialogueDocumentLine line) => document.Version == 2 ? line.SpeakerKey! : line.Speaker.Trim();
        var requested = document.Lines.GroupBy(SpeakerKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First().Speaker.Trim(), StringComparer.Ordinal);
        var keys = requested.Keys.ToArray();
        var existing = document.Version == 2
            ? await db.Characters.Where(character => keys.Contains(character.Key)).ToListAsync(cancellationToken)
            : await db.Characters.Where(character => keys.Contains(character.Name)).OrderBy(character => character.Id)
                .ToListAsync(cancellationToken);
        var speakers = existing.GroupBy(character => document.Version == 2 ? character.Key : character.Name,
                StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var (key, name) in requested)
        {
            if (speakers.TryGetValue(key, out var character) && character.Name == name) continue;
            // A renamed character must not change the name captured by an older revision.
            speakers[key] = new Character
            {
                Name = name,
                Key = document.Version == 2 && character is null ? key : Guid.NewGuid().ToString("N")
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
        return new DialogueSummary(dialogue.Id, dialogue.Title, dialogue.StartLineId, dialogue.MinimumPlayerLevel);
    }
}
