using DialogueSystem.Web.Models;

namespace DialogueSystem.Web.Services;

internal sealed class DialogueGraph(Dialogue dialogue, IReadOnlyList<DialogueChoice> choices)
{
    public int? StartLineId => dialogue.StartLineId;
    private readonly Dictionary<int, DialogueLine> _lines = dialogue.Lines.ToDictionary(line => line.Id);
    private readonly ILookup<int, DialogueChoice> _outgoing =
        choices.OrderBy(choice => choice.Position).ToLookup(choice => choice.SourceLineId);
    private readonly ILookup<int, DialogueChoice> _incoming =
        choices.OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position)
            .ToLookup(choice => choice.TargetLineId);

    public IReadOnlyList<LineSummary> GetStoryBranches() =>
        _lines.Values.Where(line => line.IsStoryBranch).OrderBy(line => line.Id).Select(Summarize).ToArray();

    public DialogueValidation Validate()
    {
        var errors = new List<string>();
        if (dialogue.StartLineId is not int startLineId || !_lines.ContainsKey(startLineId))
        {
            return new DialogueValidation(["У диалога нет стартовой реплики."], _lines.Keys.Order().ToArray());
        }

        var reachable = Traverse([startLineId], id => _outgoing[id].Select(choice => choice.TargetLineId));
        var endings = reachable.Where(id => _lines[id].IsEnding).ToArray();
        var canReachEnding = Traverse(endings, id => _incoming[id].Select(choice => choice.SourceLineId));

        if (endings.Length == 0)
        {
            errors.Add("От стартовой реплики нельзя добраться до концовки.");
        }

        foreach (var id in reachable.Order())
        {
            var line = _lines[id];
            if (line.IsEnding && _outgoing[id].Any())
            {
                errors.Add($"У конечной реплики {id} есть исходящие варианты.");
            }
            else if (!line.IsEnding && !_outgoing[id].Any())
            {
                errors.Add($"У промежуточной реплики {id} нет исходящих вариантов.");
            }
            else if (!canReachEnding.Contains(id))
            {
                errors.Add($"Из реплики {id} нельзя добраться до концовки.");
            }
        }

        foreach (var line in _lines.Values.Where(line => line.IsStoryBranch).OrderBy(line => line.Id))
        {
            if (_outgoing[line.Id].Select(choice => choice.TargetLineId).Distinct().Take(2).Count() < 2)
            {
                errors.Add($"Сюжетная развилка {line.Id} должна вести хотя бы к двум разным репликам.");
            }
        }

        return new DialogueValidation(errors, _lines.Keys.Except(reachable).Order().ToArray());
    }

    public DialogueChoice? FindChoice(int lineId, int choiceId) =>
        _lines[lineId].IsEnding ? null : _outgoing[lineId].FirstOrDefault(choice => choice.Id == choiceId);

    public PreviewStep GetPreviewStep(DialoguePreview preview, IReadOnlyList<int> unreachableLineIds)
    {
        var line = _lines[preview.CurrentLineId];
        var choices = _outgoing[line.Id].Select(choice => new PreviewChoice(choice.Id, choice.Text)).ToArray();
        return new PreviewStep(preview.Id, preview.Step, Summarize(line), choices, unreachableLineIds);
    }

    private static LineSummary Summarize(DialogueLine line) =>
        new(line.Id, line.Speaker.Name, line.Text, line.IsEnding, line.IsStoryBranch, line.SpeakerId);

    private static HashSet<int> Traverse(IEnumerable<int> startIds, Func<int, IEnumerable<int>> neighbors)
    {
        var visited = new HashSet<int>(startIds);
        var pending = new Queue<int>(visited);
        while (pending.TryDequeue(out var id))
        {
            foreach (var neighbor in neighbors(id))
            {
                if (visited.Add(neighbor))
                {
                    pending.Enqueue(neighbor);
                }
            }
        }

        return visited;
    }
}
