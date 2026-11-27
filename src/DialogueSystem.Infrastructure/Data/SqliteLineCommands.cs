using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed partial class SqliteLines
{
    public async Task<ChoiceSummary?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var choice = await FindChoiceEntityAsync(id, lineId, choiceId, ct);
        return choice is null ? null : SummarizeChoice(choice);
    }

    public async Task<EditResult<LineSummary>> CreateAsync(int id, LineChange input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await DialogueExistsAsync(id, ct))
            return new(EditStatus.NotFound);
        if (await CountLinesAsync(id, ct) >= DialogueLimits.MaximumLines)
            return new(EditStatus.Conflict, $"Допускается до {DialogueLimits.MaximumLines} реплик в диалоге.");

        var speaker = await FindCharacterAsync(input.SpeakerId, ct);
        if (speaker is null)
            return new(EditStatus.Invalid, "Говорящий персонаж не найден.");
        if (input.IsEnding && input.IsStoryBranch)
            return new(EditStatus.Invalid, "Концовка не может быть сюжетной развилкой.");

        var line = new DialogueLine
        {
            DialogueId = id, Speaker = speaker, Text = input.Text.Trim(),
            IsEnding = input.IsEnding, IsStoryBranch = input.IsStoryBranch
        };
        db.Lines.Add(line);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved, Value:
            new LineSummary(line.Id, speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, speaker.Id));
    }

    public async Task<EditResult> UpdateAsync(int id, int lineId, LineChange input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var line = await FindLineAsync(id, lineId, ct);
        if (line is null)
            return new(EditStatus.NotFound);
        if (input.Version != EditVersions.Line(line.SpeakerId, line.Text, line.IsEnding, line.IsStoryBranch))
            return new(EditStatus.Conflict, "Реплика уже изменена в другой вкладке. Обновите её перед сохранением.");
        if (await FindCharacterAsync(input.SpeakerId, ct) is null)
            return new(EditStatus.Invalid, "Говорящий персонаж не найден.");
        if (input.IsEnding && input.IsStoryBranch)
            return new(EditStatus.Invalid, "Концовка не может быть сюжетной развилкой.");
        if (input.IsEnding && await HasOutgoingAsync(id, lineId, ct))
            return new(EditStatus.Conflict, "Сначала удалите исходящие варианты ответа.");

        line.SpeakerId = input.SpeakerId;
        line.Text = input.Text.Trim();
        line.IsEnding = input.IsEnding;
        line.IsStoryBranch = input.IsStoryBranch;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> DeleteAsync(int id, int lineId, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var line = await FindLineAsync(id, lineId, ct);
        if (line is null)
            return new(EditStatus.NotFound);
        var revision = await DialogueRevisionAsync(id, ct);
        if (version != EditVersions.LineDeletion(lineId, EditVersions.Line(line.SpeakerId, line.Text, line.IsEnding, line.IsStoryBranch), revision!.Value))
            return new(EditStatus.StaleVersion, "Реплика или её связи уже изменились. Обновите контекст перед удалением.");
        if (await IsStartAsync(id, lineId, ct) || await HasIncomingAsync(id, lineId, ct))
            return new(EditStatus.Conflict, "Перенесите старт или измените входящие переходы перед удалением.");

        await DeleteOutgoingAsync(id, lineId, ct);
        db.Lines.Remove(line);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> DeleteChoiceAsync(int id, int lineId, int choiceId, string version, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var choice = await FindChoiceEntityAsync(id, lineId, choiceId, ct);
        if (choice is null) return new(EditStatus.NotFound);
        if (version != EditVersions.Choice(choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.StaleVersion, "Вариант ответа уже изменился. Обновите реплику перед удалением.");
        await DeleteChoiceAsync(id, lineId, choiceId, ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> MoveChoiceAsync(int id, int lineId, int choiceId, int direction, string version, CancellationToken ct)
    {
        if (direction is not (-1 or 1)) return new(EditStatus.Invalid, "Направление должно быть -1 или 1.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var choice = await FindChoiceEntityAsync(id, lineId, choiceId, ct);
        if (choice is null) return new(EditStatus.NotFound);
        if (version != EditVersions.Choice(choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.Conflict, "Вариант ответа уже изменился. Обновите реплику перед перемещением.");
        var neighbor = await NeighborChoiceAsync(id, lineId, choice.Position, direction, ct);
        if (neighbor is not null)
        {
            if (await PositionTakenAsync(id, lineId, 0, choice.Id, ct))
                return new(EditStatus.Conflict, "В диалоге есть вариант с недопустимой нулевой позицией.");
            await SwapChoicesAsync(choice, neighbor, ct);
        }
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult<ChoiceSummary>> SaveChoiceAsync(int id, int lineId, int? choiceId, ChoiceChange input, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var source = await FindLineAsync(id, lineId, ct);
        if (source is null)
            return new(EditStatus.NotFound);
        if (choiceId is null && await CountChoicesAsync(id, ct) >= DialogueLimits.MaximumChoices)
            return new(EditStatus.Conflict, $"Допускается до {DialogueLimits.MaximumChoices} переходов в диалоге.");

        var choice = choiceId is int existingId
            ? await FindChoiceEntityAsync(id, lineId, existingId, ct)
            : new DialogueChoice { DialogueId = id, SourceLineId = lineId, Text = input.Text.Trim() };
        if (choice is null)
            return new(EditStatus.NotFound);
        if (choiceId is not null && input.Version != EditVersions.Choice(
                choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.Conflict, "Вариант ответа уже изменён в другой вкладке. Обновите реплику перед сохранением.");
        if (source.IsEnding)
            return new(EditStatus.Conflict, "У концовки не может быть исходящих ответов.");
        if (!await LineExistsAsync(id, input.TargetLineId, ct))
            return new(EditStatus.Invalid, "Цель перехода должна принадлежать этому диалогу.");
        if (await PositionTakenAsync(id, lineId, input.Position, choice.Id, ct))
            return new(EditStatus.Conflict, "Эта позиция уже занята другим ответом.");

        choice.TargetLineId = input.TargetLineId;
        choice.Text = input.Text.Trim();
        choice.Position = input.Position;
        choice.RequiredState = DialogueChoice.NormalizeStoryState(input.RequiredState);
        choice.NextState = DialogueChoice.NormalizeStoryState(input.NextState);
        if (choiceId is null)
            db.Choices.Add(choice);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved, Value: choiceId is null ? SummarizeChoice(choice) : null);
    }

    private static ChoiceSummary SummarizeChoice(DialogueChoice choice) => new(choice.Id, choice.DialogueId,
        choice.SourceLineId, choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState);
}
