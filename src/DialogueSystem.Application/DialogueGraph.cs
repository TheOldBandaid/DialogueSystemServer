using System.Collections.Frozen;
using DialogueSystem.Domain;

namespace DialogueSystem.Application;

internal sealed record DialogueGraphLine(int Id, int SpeakerId, string Speaker, string? SpeakerKey,
    string Text, bool IsEnding, bool IsStoryBranch);
public sealed record DialogueGraphChoice(int Id, int SourceLineId, int TargetLineId, string Text, int Position,
    string? RequiredState, string? NextState);
internal readonly record struct StoryPosition(int LineId, string? State);
public sealed class DialogueGraph
{
    public string Title { get; }
    public int? StartLineId { get; }
    public int? MinimumPlayerLevel { get; }
    public long Revision { get; }
    public bool ContainsLine(int lineId) => _lines.ContainsKey(lineId);
    public bool IsEnding(int lineId) => _lines.TryGetValue(lineId, out var line) && line.IsEnding;
    private IReadOnlyList<DialogueGraphChoice> Choices { get; }
    internal IEnumerable<DialogueGraphChoice> Outgoing(int lineId) => _outgoing[lineId];

    private readonly object _validationLock = new();
    private readonly bool _hasConditions;
    private DialogueValidation? _baseValidation;
    private HashSet<StoryPosition> _basePositions = [];
    private readonly FrozenDictionary<int, DialogueGraphLine> _lines;
    private readonly ILookup<int, DialogueGraphChoice> _outgoing;
    private readonly ILookup<int, DialogueGraphChoice> _incoming;

    public DialogueGraph(Dialogue dialogue, IReadOnlyList<DialogueChoice> choices)
        : this(dialogue.Title, dialogue.StartLineId, dialogue.MinimumPlayerLevel, dialogue.Revision,
            dialogue.Lines.Select(line => new DialogueGraphLine(line.Id, line.SpeakerId, line.Speaker.Name,
                line.Speaker.Key, line.Text, line.IsEnding, line.IsStoryBranch)),
            choices.Select(choice => new DialogueGraphChoice(choice.Id, choice.SourceLineId, choice.TargetLineId,
                choice.Text, choice.Position, choice.RequiredState, choice.NextState))) { }

    private DialogueGraph(string title, int? start, int? level, long revision,
        IEnumerable<DialogueGraphLine> lines, IEnumerable<DialogueGraphChoice> choices)
    {
        Title = title;
        StartLineId = start;
        MinimumPlayerLevel = level;
        Revision = revision;
        _lines = lines.ToFrozenDictionary(line => line.Id);
        Choices = Array.AsReadOnly(choices.OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position).ToArray());
        _outgoing = Choices.ToLookup(choice => choice.SourceLineId);
        _incoming = Choices.ToLookup(choice => choice.TargetLineId);
        _hasConditions = Choices.Any(choice => choice.RequiredState is not null);
    }

    public DialogueDocument ToDocument() => new(2, Title, StartLineId,
        _lines.Values.OrderBy(line => line.Id).Select(line => new DialogueDocumentLine(
            line.Id, line.Speaker, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerKey)).ToArray(),
        Choices.Select(choice => new DialogueDocumentChoice(choice.SourceLineId, choice.TargetLineId, choice.Text,
            choice.Position, choice.RequiredState, choice.NextState)).ToArray(), MinimumPlayerLevel);

    public DialogueValidation Validate(DialoguePreview? preview = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_validationLock)
        {
            ct.ThrowIfCancellationRequested();
            _baseValidation ??= ValidateUncached(null, ct);
            if (preview is null || _baseValidation.Errors.Count != 0 ||
                _basePositions.Contains(new(preview.CurrentLineId, _hasConditions ? preview.StoryState : null)))
                return _baseValidation;
            return ValidateUncached(preview, ct);
        }
    }

    private DialogueValidation ValidateUncached(DialoguePreview? preview, CancellationToken ct)
    {
        try
        {
            if (_lines.Count > DialogueLimits.MaximumLines || Choices.Count > DialogueLimits.MaximumChoices)
                throw new ValidationLimitException();
            return ValidateCore(preview, ct);
        }
        catch (ValidationLimitException)
        {
            return BuildValidation([new("validation-limit",
                "Диалог слишком сложен для полной проверки. Сократите число реплик, переходов или состояний.")], [])
                with { Complete = false };
        }
    }

    private DialogueValidation ValidateCore(DialoguePreview? preview, CancellationToken ct)
    {
        if (StartLineId is not int startLineId || !_lines.ContainsKey(startLineId))
        {
            return BuildValidation([new("missing-start", "У диалога нет стартовой реплики.")], _lines.Keys.Order().ToArray());
        }

        var issues = new List<DialogueValidationIssue>();
        var reachable = _hasConditions
            ? ValidateStoryPaths(startLineId, preview, issues, ct)
            : ValidatePaths(startLineId, preview, issues, ct);

        foreach (var line in _lines.Values.Where(line => line.IsStoryBranch).OrderBy(line => line.Id))
        {
            ct.ThrowIfCancellationRequested();
            if (_outgoing[line.Id].Select(choice => choice.TargetLineId).Distinct().Take(2).Count() < 2)
            {
                issues.Add(new("story-branch-needs-targets",
                    $"Сюжетная развилка {line.Id} должна вести хотя бы к двум разным репликам.", line.Id));
            }
        }

        return BuildValidation(issues, _lines.Keys.Except(reachable).Order().ToArray());
    }

    private HashSet<int> ValidatePaths(int startLineId, DialoguePreview? preview,
        List<DialogueValidationIssue> issues, CancellationToken ct)
    {
        var reachable = Traverse([startLineId], id => _outgoing[id].Select(choice => choice.TargetLineId), ct);
        var reachableFromStart = reachable.ToHashSet();
        if (preview is null) _basePositions = reachable.Select(id => new StoryPosition(id, null)).ToHashSet();
        if (preview is not null && !reachable.Contains(preview.CurrentLineId))
            reachable.UnionWith(Traverse([preview.CurrentLineId], id => _outgoing[id].Select(choice => choice.TargetLineId), ct));
        var endings = reachable.Where(id => _lines[id].IsEnding).ToArray();
        var canReachEnding = Traverse(endings, id => _incoming[id].Select(choice => choice.SourceLineId), ct);

        if (endings.Length == 0)
        {
            issues.Add(NoReachableEnding(startLineId));
        }

        foreach (var id in reachable.Order())
        {
            ct.ThrowIfCancellationRequested();
            var line = _lines[id];
            if (line.IsEnding && _outgoing[id].Any())
            {
                issues.Add(EndingHasChoices(id));
            }
            else if (!line.IsEnding && !_outgoing[id].Any())
            {
                issues.Add(new("line-without-choices", $"У промежуточной реплики {id} нет исходящих вариантов.", id));
            }
            else if (!canReachEnding.Contains(id))
            {
                issues.Add(new("no-path-to-ending", $"Из реплики {id} нельзя добраться до концовки.", id));
            }
        }

        return reachableFromStart;
    }

    private HashSet<int> ValidateStoryPaths(int startLineId, DialoguePreview? preview,
        List<DialogueValidationIssue> issues, CancellationToken ct)
    {
        var walk = DialogueTraversal.Explore(this, ct);
        if (!walk.Complete) throw new ValidationLimitException();
        var reachable = walk.Positions.ToHashSet();
        if (preview is null) _basePositions = reachable;
        var reachableLineIds = reachable.Select(position => position.LineId).ToHashSet();
        if (!reachableLineIds.Any(id => _lines[id].IsEnding)) issues.Add(NoReachableEnding(startLineId));

        if (preview is not null && !reachable.Contains(new(preview.CurrentLineId, preview.StoryState)))
        {
            walk.ContinueFrom(new(preview.CurrentLineId, preview.StoryState), ct);
            if (!walk.Complete) throw new ValidationLimitException();
            reachable = walk.Positions.ToHashSet();
        }
        var canReachEnding = walk.CanReachEnding(ct);
        var endings = walk.Positions.Where(position => IsEnding(position.LineId));

        foreach (var endingId in endings.Select(position => position.LineId).Distinct().Order())
        {
            if (_outgoing[endingId].Any())
            {
                issues.Add(EndingHasChoices(endingId));
            }
        }

        foreach (var position in reachable.Except(canReachEnding)
                     .OrderBy(position => position.LineId).ThenBy(position => position.State, StringComparer.Ordinal))
        {
            var state = position.State is null ? "с пустым состоянием сюжета" : $"при состоянии «{position.State}»";
            issues.Add(new("state-no-path-to-ending",
                $"Из реплики {position.LineId} {state} нельзя добраться до концовки.", position.LineId));
        }

        return reachableLineIds;
    }

    private IEnumerable<DialogueGraphChoice> AvailableChoices(int lineId, string? state) =>
        _lines[lineId].IsEnding ? [] :
            _outgoing[lineId].Where(choice => DialogueStoryRules.Allows(choice.RequiredState, state));

    private static DialogueValidationIssue NoReachableEnding(int startLineId) =>
        new("no-reachable-ending", "От стартовой реплики нельзя добраться до концовки.", startLineId);

    private static DialogueValidationIssue EndingHasChoices(int lineId) =>
        new("ending-has-choices", $"У конечной реплики {lineId} есть исходящие варианты.", lineId);

    private static DialogueValidation BuildValidation(
        IReadOnlyList<DialogueValidationIssue> issues, IReadOnlyList<int> unreachableLineIds)
    {
        var visible = issues.Count <= DialogueLimits.MaximumValidationIssues ? issues :
            issues.Take(DialogueLimits.MaximumValidationIssues - 1).Append(new DialogueValidationIssue(
                "issues-truncated", $"Показаны первые {DialogueLimits.MaximumValidationIssues - 1} ошибки из {issues.Count}."))
                .ToArray();
        return new(visible.Select(issue => issue.Message).ToArray(), unreachableLineIds) { Issues = visible };
    }

    public DialogueGraphChoice? FindChoice(DialoguePreview preview, int choiceId) =>
        AvailableChoices(preview.CurrentLineId, preview.StoryState).FirstOrDefault(choice => choice.Id == choiceId);

    public PreviewStep GetPreviewStep(DialoguePreview preview, IReadOnlyList<int> unreachableLineIds)
    {
        var line = _lines[preview.CurrentLineId];
        var options = AvailableChoices(line.Id, preview.StoryState)
            .Select(choice => new PreviewChoice(choice.Id, choice.Text)).ToArray();
        return new PreviewStep(preview.Id, preview.Step, Summarize(line), options, unreachableLineIds, preview.StoryState);
    }

    private static LineSummary Summarize(DialogueGraphLine line) =>
        new(line.Id, line.Speaker, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId);

    private static HashSet<T> Traverse<T>(IEnumerable<T> start, Func<T, IEnumerable<T>> neighbors,
        CancellationToken ct) where T : notnull
    {
        var visited = new HashSet<T>(start);
        if (visited.Count > DialogueLimits.MaximumPositions) throw new ValidationLimitException();
        var pending = new Queue<T>(visited);
        var examined = 0;
        while (pending.TryDequeue(out var item))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var neighbor in neighbors(item))
            {
                if (++examined > DialogueLimits.MaximumExaminedChoices) throw new ValidationLimitException();
                if (visited.Add(neighbor))
                {
                    if (visited.Count > DialogueLimits.MaximumPositions) throw new ValidationLimitException();
                    pending.Enqueue(neighbor);
                }
            }
        }

        return visited;
    }

    private sealed class ValidationLimitException : Exception { }
}
