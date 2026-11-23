using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public sealed record LineChange(int SpeakerId, string Text, bool IsEnding, bool IsStoryBranch);
public sealed record ChoiceChange(
    int TargetLineId, string Text, int Position, string? RequiredState, string? NextState);

public enum EditStatus { Saved, NotFound, Invalid, Conflict }

public sealed record EditResult(EditStatus Status, string? Detail = null,
    LineSummary? NewLine = null, DialogueChoice? NewChoice = null);

public interface ILineStore
{
    Task<IStorageTransaction> BeginAsync(CancellationToken ct);
    Task<bool> DialogueExistsAsync(int id, CancellationToken ct);
    Task<Dialogue?> FindDialogueAsync(int id, CancellationToken ct);
    Task<List<LineSummary>> ListAsync(int id, CancellationToken ct);
    Task<DialogueLine?> FindLineAsync(int id, int lineId, CancellationToken ct);
    Task<bool> LineExistsAsync(int id, int lineId, CancellationToken ct);
    Task<Character?> FindCharacterAsync(int id, CancellationToken ct);
    Task<bool> IsStartAsync(int id, int lineId, CancellationToken ct);
    Task<bool> HasIncomingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> HasOutgoingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> PositionTakenAsync(int id, int lineId, int position, int exceptChoice, CancellationToken ct);
    Task<DialogueChoice?> FindChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct);
    Task DeleteOutgoingAsync(int id, int lineId, CancellationToken ct);
    Task<bool> DeleteChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct);
    void AddLine(DialogueLine line);
    void RemoveLine(DialogueLine line);
    void AddChoice(DialogueChoice choice);
    Task SaveAsync(CancellationToken ct);
}

public sealed class LineEditor(ILineStore store)
{
    public async Task<List<LineSummary>?> ListAsync(int id, CancellationToken ct) =>
        await store.DialogueExistsAsync(id, ct) ? await store.ListAsync(id, ct) : null;

    public async Task<EditResult> CreateAsync(int id, LineChange input, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        if (!await store.DialogueExistsAsync(id, ct))
            return new(EditStatus.NotFound);

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

    public async Task<EditResult> SetStartAsync(int id, int? lineId, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var dialogue = await store.FindDialogueAsync(id, ct);
        if (dialogue is null)
            return new(EditStatus.NotFound);
        if (lineId is int start && !await store.LineExistsAsync(id, start, ct))
            return new(EditStatus.Invalid, "Стартовая реплика должна принадлежать этому диалогу.");

        dialogue.StartLineId = lineId;
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public async Task<EditResult> DeleteAsync(int id, int lineId, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var line = await store.FindLineAsync(id, lineId, ct);
        if (line is null)
            return new(EditStatus.NotFound);
        if (await store.IsStartAsync(id, lineId, ct) || await store.HasIncomingAsync(id, lineId, ct))
            return new(EditStatus.Conflict, "Перенесите старт или измените входящие переходы перед удалением.");

        await store.DeleteOutgoingAsync(id, lineId, ct);
        store.RemoveLine(line);
        await store.SaveAsync(ct);
        await transaction.CommitAsync(ct);
        return new(EditStatus.Saved);
    }

    public Task<bool> DeleteChoiceAsync(int id, int lineId, int choiceId, CancellationToken ct) =>
        store.DeleteChoiceAsync(id, lineId, choiceId, ct);

    public async Task<EditResult> SaveChoiceAsync(int id, int lineId, int? choiceId, ChoiceChange input, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var source = await store.FindLineAsync(id, lineId, ct);
        if (source is null)
            return new(EditStatus.NotFound);

        var choice = choiceId is int existingId
            ? await store.FindChoiceAsync(id, lineId, existingId, ct)
            : new DialogueChoice { DialogueId = id, SourceLineId = lineId, Text = input.Text.Trim() };
        if (choice is null)
            return new(EditStatus.NotFound);
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
        return new(EditStatus.Saved, NewChoice: choiceId is null ? choice : null);
    }
}
