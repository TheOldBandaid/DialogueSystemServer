using DialogueSystem.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Data;

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
        var names = document.Lines.Select(line => line.Speaker.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        var existing = await db.Characters.Where(character => names.Contains(character.Name))
            .OrderBy(character => character.Id).ToListAsync(cancellationToken);
        var speakers = existing.GroupBy(character => character.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (!speakers.ContainsKey(name))
            {
                speakers.Add(name, new Character { Name = name });
            }
        }

        var dialogue = new Dialogue
        {
            Title = document.Title.Trim(),
            MinimumPlayerLevel = document.MinimumPlayerLevel
        };
        var lines = document.Lines.ToDictionary(line => line.Id, line => new DialogueLine
        {
            Speaker = speakers[line.Speaker.Trim()],
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
