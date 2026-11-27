using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public sealed record LineChange(int SpeakerId, string Text, bool IsEnding, bool IsStoryBranch, string? Version = null);
public sealed record ChoiceChange(
    int TargetLineId, string Text, int Position, string? RequiredState, string? NextState, string? Version = null);

public enum EditStatus { Saved, NotFound, Invalid, Conflict, StaleVersion }

public sealed record EditResult(EditStatus Status, string? Detail = null,
    LineSummary? NewLine = null, ChoiceSummary? NewChoice = null);

public interface ILineStore
{
    Task<LineResource?> ReadLineAsync(int id, int lineId, CancellationToken ct);
    Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null);
    Task<IStorageTransaction> BeginAsync(CancellationToken ct);
    Task<bool> DialogueExistsAsync(int id, CancellationToken ct);
    Task<int> CountLinesAsync(int id, CancellationToken ct);
    Task<int> CountChoicesAsync(int id, CancellationToken ct);
    Task<long?> DialogueRevisionAsync(int id, CancellationToken ct);
    Task<CatalogPage<LineReference>?> PageAsync(int id, string? query, int? before, bool branchesOnly, CancellationToken ct);
    Task<DialogueLine?> FindLineAsync(int id, int lineId, CancellationToken ct);
    Task<bool> LineExistsAsync(int id, int lineId, CancellationToken ct);
    Task<Character?> FindCharacterAsync(int id, CancellationToken ct);
    Task<bool> IsStartAsync(int id, int lineId, CancellationToken ct);
    Task<bool> HasIncomingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> HasOutgoingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> PositionTakenAsync(int id, int lineId, int position, int exceptChoice, CancellationToken ct);
    Task<DialogueChoice?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct);
    Task<DialogueChoice?> NeighborChoiceAsync(int id, int lineId, int position, int direction, CancellationToken ct);
    Task SwapChoicesAsync(DialogueChoice first, DialogueChoice second, CancellationToken ct);
    Task DeleteOutgoingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> DeleteChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct);
    void AddLine(DialogueLine line);
    void RemoveLine(DialogueLine line);
    void AddChoice(DialogueChoice choice);
    Task SaveAsync(CancellationToken ct);
}

public sealed class LineEditor(ILineStore store)
{
    public Task<LineResource?> ReadAsync(int id, int lineId, CancellationToken ct) =>
        store.ReadLineAsync(id, lineId, ct);

    public Task<LineContext?> ContextAsync(int id, int lineId, CancellationToken ct,
        int? outgoingAfter = null, int? incomingAfterLine = null, int? incomingAfterPosition = null) =>
        store.ContextAsync(id, lineId, ct, outgoingAfter, incomingAfterLine, incomingAfterPosition);

    public Task<CatalogPage<LineReference>?> PageAsync(int id, string? query, int? before, bool branchesOnly, CancellationToken ct) =>
        store.PageAsync(id, query, before, branchesOnly, ct);

    public async Task<ChoiceSummary?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct)
    {
        var choice = await store.FindChoiceAsync(id, lineId, choiceId, ct);
        return choice is null ? null : SummarizeChoice(choice);
    }

    public async Task<EditResult> CreateAsync(int id, LineChange input, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        if (!await store.DialogueExistsAsync(id, ct))
            return new(EditStatus.NotFound);
        if (await store.CountLinesAsync(id, ct) >= DialogueLimits.MaximumLines)
            return new(EditStatus.Conflict, $"Допускается до {DialogueLimits.MaximumLines} реплик в диалоге.");

        var speaker = await store.FindCharacterAsync(input.SpeakerId, ct);
        if (speaker is null)
            return new(EditStatus.Invalid, "Говорящий персонаж не найден.");
        if (input.IsEnding && input.IsStoryBranch)
            return new(EditStatus.Invalid, "Концовка не может быть сюжетной развилкой.");

        var line = new DialogueLine
        {
            DialogueId = id, Speaker = speaker, Text = input.Text.Trim(),
            IsEnding = input.IsEnding, IsStoryBranch = input.IsStoryBranch
        };
        store.AddLine(line);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved, NewLine:
            new LineSummary(line.Id, speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, speaker.Id));
    }

    public async Task<EditResult> UpdateAsync(int id, int lineId, LineChange input, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var line = await store.FindLineAsync(id, lineId, ct);
        if (line is null)
            return new(EditStatus.NotFound);
        if (input.Version != EditVersions.Line(line.SpeakerId, line.Text, line.IsEnding, line.IsStoryBranch))
            return new(EditStatus.Conflict, "Реплика уже изменена в другой вкладке. Обновите её перед сохранением.");
        if (await store.FindCharacterAsync(input.SpeakerId, ct) is null)
            return new(EditStatus.Invalid, "Говорящий персонаж не найден.");
        if (input.IsEnding && input.IsStoryBranch)
            return new(EditStatus.Invalid, "Концовка не может быть сюжетной развилкой.");
        if (input.IsEnding && await store.HasOutgoingAsync(id, lineId, ct))
            return new(EditStatus.Conflict, "Сначала удалите исходящие варианты ответа.");

        line.SpeakerId = input.SpeakerId;
        line.Text = input.Text.Trim();
        line.IsEnding = input.IsEnding;
        line.IsStoryBranch = input.IsStoryBranch;
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> DeleteAsync(int id, int lineId, string version, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var line = await store.FindLineAsync(id, lineId, ct);
        if (line is null)
            return new(EditStatus.NotFound);
        var revision = await store.DialogueRevisionAsync(id, ct);
        if (version != EditVersions.LineDeletion(lineId, EditVersions.Line(line.SpeakerId, line.Text, line.IsEnding, line.IsStoryBranch), revision!.Value))
            return new(EditStatus.StaleVersion, "Реплика или её связи уже изменились. Обновите контекст перед удалением.");
        if (await store.IsStartAsync(id, lineId, ct) || await store.HasIncomingAsync(id, lineId, ct))
            return new(EditStatus.Conflict, "Перенесите старт или измените входящие переходы перед удалением.");

        await store.DeleteOutgoingAsync(id, lineId, ct);
        store.RemoveLine(line);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> DeleteChoiceAsync(int id, int lineId, int choiceId, string version, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var choice = await store.FindChoiceAsync(id, lineId, choiceId, ct);
        if (choice is null) return new(EditStatus.NotFound);
        if (version != EditVersions.Choice(choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.StaleVersion, "Вариант ответа уже изменился. Обновите реплику перед удалением.");
        await store.DeleteChoiceAsync(id, lineId, choiceId, ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> MoveChoiceAsync(int id, int lineId, int choiceId, int direction, string version, CancellationToken ct)
    {
        if (direction is not (-1 or 1)) return new(EditStatus.Invalid, "Направление должно быть -1 или 1.");
        await using var transaction = await store.BeginAsync(ct);
        var choice = await store.FindChoiceAsync(id, lineId, choiceId, ct);
        if (choice is null) return new(EditStatus.NotFound);
        if (version != EditVersions.Choice(choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.Conflict, "Вариант ответа уже изменился. Обновите реплику перед перемещением.");
        var neighbor = await store.NeighborChoiceAsync(id, lineId, choice.Position, direction, ct);
        if (neighbor is not null)
        {
            if (await store.PositionTakenAsync(id, lineId, 0, choice.Id, ct))
                return new(EditStatus.Conflict, "В диалоге есть вариант с недопустимой нулевой позицией.");
            await store.SwapChoicesAsync(choice, neighbor, ct);
        }
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> SaveChoiceAsync(int id, int lineId, int? choiceId, ChoiceChange input, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var source = await store.FindLineAsync(id, lineId, ct);
        if (source is null)
            return new(EditStatus.NotFound);
        if (choiceId is null && await store.CountChoicesAsync(id, ct) >= DialogueLimits.MaximumChoices)
            return new(EditStatus.Conflict, $"Допускается до {DialogueLimits.MaximumChoices} переходов в диалоге.");

        var choice = choiceId is int existingId
            ? await store.FindChoiceAsync(id, lineId, existingId, ct)
            : new DialogueChoice { DialogueId = id, SourceLineId = lineId, Text = input.Text.Trim() };
        if (choice is null)
            return new(EditStatus.NotFound);
        if (choiceId is not null && input.Version != EditVersions.Choice(
                choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState))
            return new(EditStatus.Conflict, "Вариант ответа уже изменён в другой вкладке. Обновите реплику перед сохранением.");
        if (source.IsEnding)
            return new(EditStatus.Conflict, "У концовки не может быть исходящих ответов.");
        if (!await store.LineExistsAsync(id, input.TargetLineId, ct))
            return new(EditStatus.Invalid, "Цель перехода должна принадлежать этому диалогу.");
        if (await store.PositionTakenAsync(id, lineId, input.Position, choice.Id, ct))
            return new(EditStatus.Conflict, "Эта позиция уже занята другим ответом.");

        choice.TargetLineId = input.TargetLineId;
        choice.Text = input.Text.Trim();
        choice.Position = input.Position;
        choice.RequiredState = DialogueChoice.NormalizeStoryState(input.RequiredState);
        choice.NextState = DialogueChoice.NormalizeStoryState(input.NextState);
        if (choiceId is null)
            store.AddChoice(choice);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved, NewChoice: choiceId is null ? SummarizeChoice(choice) : null);
    }

    private static ChoiceSummary SummarizeChoice(DialogueChoice choice) => new(choice.Id, choice.DialogueId,
        choice.SourceLineId, choice.TargetLineId, choice.Text, choice.Position, choice.RequiredState, choice.NextState);
}
