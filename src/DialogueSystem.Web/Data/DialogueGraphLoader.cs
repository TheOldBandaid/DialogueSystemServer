using DialogueSystem.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Data;

internal static class DialogueGraphLoader
{
    public static async Task<DialogueGraph?> LoadAsync(
        DialogueDbContext db, int dialogueId, CancellationToken cancellationToken)
    {
        var dialogue = await db.Dialogues.AsNoTracking()
            .Include(dialogue => dialogue.Lines).ThenInclude(line => line.Speaker)
            .SingleOrDefaultAsync(dialogue => dialogue.Id == dialogueId, cancellationToken);
        if (dialogue is null)
        {
            return null;
        }

        var choices = await db.Choices.AsNoTracking().Where(choice => choice.DialogueId == dialogueId)
            .ToListAsync(cancellationToken);
        return new DialogueGraph(dialogue, choices);
    }
}
