using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public sealed class DialogueGraph(Dialogue dialogue, IReadOnlyList<DialogueChoice> choices)
{
    public int? StartLineId => dialogue.StartLineId;
    public int? MinimumPlayerLevel => dialogue.MinimumPlayerLevel;
    private readonly Dictionary<int, DialogueLine> _lines = dialogue.Lines.ToDictionary(line => line.Id);
    private readonly ILookup<int, DialogueChoice> _outgoing =
        choices.OrderBy(choice => choice.Position).ToLookup(choice => choice.SourceLineId);
    private readonly ILookup<int, DialogueChoice> _incoming =
        choices.OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position)
            .ToLookup(choice => choice.TargetLineId);

    public IReadOnlyList<LineSummary> GetStoryBranches() =>
        _lines.Values.Where(line => line.IsStoryBranch).OrderBy(line => line.Id).Select(Summarize).ToArray();

    public DialogueDocument ToDocument() => new(
        1, dialogue.Title, dialogue.StartLineId,
        _lines.Values.OrderBy(line => line.Id)
            .Select(line => new DialogueDocumentLine(
                line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch)).ToArray(),
        choices.OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position)
            .Select(choice => new DialogueDocumentChoice(
                choice.SourceLineId, choice.TargetLineId, choice.Text, choice.Position,
                choice.RequiredState, choice.NextState)).ToArray(),
        dialogue.MinimumPlayerLevel);

    public DialogueValidation Validate(DialoguePreview? preview = null)
    {
        if (dialogue.StartLineId is not int startLineId || !_lines.ContainsKey(startLineId))
        {
            return BuildValidation([new("missing-start", "У диалога нет стартовой реплики.")], _lines.Keys.Order().ToArray());
        }

        var issues = new List<DialogueValidationIssue>();
        var reachable = choices.Any(choice => choice.RequiredState is not null)
            ? ValidateStoryPaths(startLineId, preview, issues)
            : ValidatePaths(startLineId, issues);

        foreach (var line in _lines.Values.Where(line => line.IsStoryBranch).OrderBy(line => line.Id))
        {
            if (_outgoing[line.Id].Select(choice => choice.TargetLineId).Distinct().Take(2).Count() < 2)
            {
                issues.Add(new("story-branch-needs-targets",
                    $"Сюжетная развилка {line.Id} должна вести хотя бы к двум разным репликам.", line.Id));
            }
        }

        return BuildValidation(issues, _lines.Keys.Except(reachable).Order().ToArray());
    }

    private HashSet<int> ValidatePaths(int startLineId, List<DialogueValidationIssue> issues)
    {
        var reachable = Traverse([startLineId], id => _outgoing[id].Select(choice => choice.TargetLineId));
        var endings = reachable.Where(id => _lines[id].IsEnding).ToArray();
        var canReachEnding = Traverse(endings, id => _incoming[id].Select(choice => choice.SourceLineId));

        if (endings.Length == 0)
        {
            issues.Add(NoReachableEnding(startLineId));
        }

        foreach (var id in reachable.Order())
        {
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

        return reachable;
    }

    private HashSet<int> ValidateStoryPaths(int startLineId, DialoguePreview? preview, List<DialogueValidationIssue> issues)
    {
        var reachable = Traverse([new StoryPosition(startLineId, null)], NextPositions);
        var reachableLineIds = reachable.Select(position => position.LineId).ToHashSet();
        if (!reachableLineIds.Any(id => _lines[id].IsEnding))
        {
            issues.Add(NoReachableEnding(startLineId));
        }

        if (preview is not null)
        {
            var current = new StoryPosition(preview.CurrentLineId, preview.StoryState);
            if (!reachable.Contains(current))
            {
                reachable.UnionWith(Traverse([current], NextPositions));
            }
        }

        var previousPositions = reachable
            .SelectMany(position => NextPositions(position).Select(next => (Previous: position, Next: next)))
            .ToLookup(transition => transition.Next, transition => transition.Previous);
        var endings = reachable.Where(position => _lines[position.LineId].IsEnding).ToArray();
        var canReachEnding = Traverse(endings, position => previousPositions[position]);

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

    private IEnumerable<StoryPosition> NextPositions(StoryPosition position) =>
        AvailableChoices(position.LineId, position.State)
            .Select(choice => new StoryPosition(choice.TargetLineId, DialogueStoryRules.Advance(choice.NextState, position.State)));

    private IEnumerable<DialogueChoice> AvailableChoices(int lineId, string? state) =>
        _lines[lineId].IsEnding ? [] :
            _outgoing[lineId].Where(choice => DialogueStoryRules.Allows(choice.RequiredState, state));

    private static DialogueValidationIssue NoReachableEnding(int startLineId) =>
        new("no-reachable-ending", "От стартовой реплики нельзя добраться до концовки.", startLineId);

    private static DialogueValidationIssue EndingHasChoices(int lineId) =>
        new("ending-has-choices", $"У конечной реплики {lineId} есть исходящие варианты.", lineId);

    private static DialogueValidation BuildValidation(
        IReadOnlyList<DialogueValidationIssue> issues, IReadOnlyList<int> unreachableLineIds) =>
        new(issues.Select(issue => issue.Message).ToArray(), unreachableLineIds) { Issues = issues };

    public DialogueChoice? FindChoice(DialoguePreview preview, int choiceId) =>
        AvailableChoices(preview.CurrentLineId, preview.StoryState).FirstOrDefault(choice => choice.Id == choiceId);

    public PreviewStep GetPreviewStep(DialoguePreview preview, IReadOnlyList<int> unreachableLineIds)
    {
        var line = _lines[preview.CurrentLineId];
        var options = AvailableChoices(line.Id, preview.StoryState)
            .Select(choice => new PreviewChoice(choice.Id, choice.Text)).ToArray();
        return new PreviewStep(preview.Id, preview.Step, Summarize(line), options, unreachableLineIds, preview.StoryState);
    }

    private static LineSummary Summarize(DialogueLine line) =>
        new(line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId);

    private static HashSet<T> Traverse<T>(IEnumerable<T> start, Func<T, IEnumerable<T>> neighbors) where T : notnull
    {
        var visited = new HashSet<T>(start);
        var pending = new Queue<T>(visited);
        while (pending.TryDequeue(out var item))
        {
            foreach (var neighbor in neighbors(item))
            {
                if (visited.Add(neighbor))
                {
                    pending.Enqueue(neighbor);
                }
            }
        }

        return visited;
    }

    private readonly record struct StoryPosition(int LineId, string? State);
}
