using DialogueSystem.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Web.Data;

internal static class DemoDialogue
{
    public static async Task SeedAsync(DialogueDbContext db, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Dialogues.AnyAsync(cancellationToken))
        {
            return;
        }

        var marta = new Character { Name = "Марта" };
        var dialogue = new Dialogue { Title = "Ключ от архива" };
        dialogue.Lines.AddRange([
            new() { Speaker = marta, Text = "В архиве остались записи о пропавшей экспедиции. У меня есть ключ." },
            new() { Speaker = marta, Text = "Ключ открывает восточное хранилище. Там лежат журналы экспедиции." },
            new() { Speaker = marta, Text = "Я могу отдать ключ. Возьмёшь его?", IsStoryBranch = true },
            new() { Speaker = marta, Text = "Держи ключ. Вход в хранилище — через восточную лестницу.", IsEnding = true },
            new() { Speaker = marta, Text = "Оставлю ключ у себя. Мы можем вернуться к разговору позже.", IsEnding = true }
        ]);
        db.Dialogues.Add(dialogue);
        await db.SaveChangesAsync(cancellationToken);
        dialogue.StartLineId = dialogue.Lines[0].Id;

        db.Choices.AddRange(
            Link(0, 1, 1, "Для чего нужен ключ?"),
            Link(0, 2, 2, "Что вы предлагаете?"),
            Link(1, 2, 1, "Вы готовы его отдать?"),
            Link(2, 3, 1, "Возьму ключ"),
            Link(2, 4, 2, "Оставлю ключ у вас"));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        DialogueChoice Link(int source, int target, int position, string text) => new()
        {
            DialogueId = dialogue.Id,
            SourceLineId = dialogue.Lines[source].Id,
            TargetLineId = dialogue.Lines[target].Id,
            Position = position,
            Text = text
        };
    }
}
