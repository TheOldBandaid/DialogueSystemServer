namespace DialogueSystem.Application;

public static class DialogueRouteAnalysis
{
    private const int MaximumPositions = 10_000;
    private const int MaximumExaminedChoices = 100_000;
    private const int MaximumExamples = 50;

    public static DialogueAnalysisReport Analyze(DialogueDocument document, CancellationToken ct = default)
    {
        var walk = Explore(document, ct);
        var reachableLines = walk.Visits.Keys.Select(position => position.LineId).ToHashSet();
        var endings = document.Lines.Where(line => line.IsEnding).OrderBy(line => line.Id)
            .Select(line => DescribeEnding(walk, line)).ToArray();

        var unreachable = walk.Complete
            ? document.Lines.Where(line => !reachableLines.Contains(line.Id)).OrderBy(line => line.Id).ToArray()
            : [];
        var unavailable = walk.Complete
            ? document.Choices.Where(choice => !walk.AvailableChoices.Contains(ChoiceKey.From(choice)))
                .OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position).ToArray()
            : [];
        var trapped = walk.Complete
            ? FindTrappedPositions(walk, ct)
            : [];

        return new DialogueAnalysisReport(document.Title, walk.StartLineId, document.MinimumPlayerLevel, walk.Complete,
            document.Lines.Count, reachableLines.Count, walk.Visits.Count, document.Choices.Count,
            walk.AvailableChoices.Count, walk.Complete ? unreachable.Length : null,
            walk.Complete ? unavailable.Length : null, walk.Complete ? trapped.Length : null, endings,
            unreachable.Take(MaximumExamples).Select(Reference).ToArray(),
            unavailable.Take(MaximumExamples).Select(choice => ExplainUnavailable(choice, walk, reachableLines)).ToArray(),
            trapped.Take(MaximumExamples).Select(position => new TrappedDialoguePosition(position.LineId, position.State))
                .ToArray());
    }

    public static DialogueRouteResult FindShortestRoute(
        DialogueDocument document, int endingId, CancellationToken ct = default)
    {
        var walk = Explore(document, ct);
        if (!walk.FirstEndingPositions.TryGetValue(endingId, out var ending))
            return new(walk.Complete, null);

        var steps = new List<DialogueRouteStep>();
        var position = ending;
        while (walk.Visits[position] is { Previous: Position previous, Answer: { } answer })
        {
            steps.Add(new(previous.LineId, position.LineId, answer.Position, answer.Text, answer.RequiredState,
                previous.State, position.State));
            position = previous;
        }

        steps.Reverse();
        return new(walk.Complete, new(position.LineId, ending.LineId, ending.State, steps));
    }

    private static Exploration Explore(DialogueDocument document, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lines = document.Lines.ToDictionary(line => line.Id);
        if (document.StartLineId is not int startLineId || !lines.ContainsKey(startLineId))
            return new(lines, null);

        var walk = new Exploration(lines, startLineId);
        var outgoing = document.Choices.OrderBy(choice => choice.Position).ToLookup(choice => choice.SourceLineId);
        var pending = new Queue<Position>();
        VisitPosition(walk, pending, new(startLineId, null), new(0));
        var examined = 0;

        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            if (lines[position.LineId].IsEnding)
                continue;

            var distance = walk.Visits[position].Distance + 1;
            foreach (var choice in outgoing[position.LineId])
            {
                if (++examined > MaximumExaminedChoices)
                    return walk;

                if (!DialogueStoryRules.Allows(choice.RequiredState, position.State) ||
                    !lines.ContainsKey(choice.TargetLineId))
                    continue;

                var next = new Position(choice.TargetLineId, DialogueStoryRules.Advance(choice.NextState, position.State));
                if (!walk.Visits.TryGetValue(next, out var visit))
                {
                    if (walk.Visits.Count == MaximumPositions)
                        return walk;

                    visit = new Visit(distance, position, choice);
                    VisitPosition(walk, pending, next, visit);
                }

                visit.Incoming.Add(position);
                walk.AvailableChoices.Add(ChoiceKey.From(choice));
            }
        }

        walk.Complete = true;
        return walk;
    }

    private static void VisitPosition(Exploration walk, Queue<Position> pending, Position position, Visit visit)
    {
        walk.Visits.Add(position, visit);
        pending.Enqueue(position);
        if (walk.Lines[position.LineId].IsEnding)
            walk.FirstEndingPositions.TryAdd(position.LineId, position);
    }

    private static DialogueEndingAnalysis DescribeEnding(Exploration walk, DialogueDocumentLine line) =>
        walk.FirstEndingPositions.TryGetValue(line.Id, out var ending)
            ? new(Reference(line), walk.Visits[ending].Distance, true)
            : new(Reference(line), null, walk.Complete ? false : null);

    private static Position[] FindTrappedPositions(Exploration walk, CancellationToken ct)
    {
        var canFinish = walk.Visits.Keys.Where(position => walk.Lines[position.LineId].IsEnding).ToHashSet();
        var pending = new Queue<Position>(canFinish);
        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var previous in walk.Visits[position].Incoming)
            {
                if (canFinish.Add(previous))
                    pending.Enqueue(previous);
            }
        }

        return walk.Visits.Keys.Where(position => !canFinish.Contains(position))
            .OrderBy(position => position.LineId).ThenBy(position => position.State, StringComparer.Ordinal).ToArray();
    }

    private static UnavailableDialogueChoice ExplainUnavailable(
        DialogueDocumentChoice choice, Exploration walk, HashSet<int> reachableLines)
    {
        string reason;
        if (!reachableLines.Contains(choice.SourceLineId))
            reason = "source-unreachable";
        else if (walk.Lines[choice.SourceLineId].IsEnding)
            reason = "terminal-source";
        else if (!walk.Lines.ContainsKey(choice.TargetLineId))
            reason = "target-missing";
        else
            reason = "required-state-unreachable";

        return new(choice.SourceLineId, choice.TargetLineId, choice.Position, choice.Text, choice.RequiredState, reason);
    }

    private static DialogueLineReference Reference(DialogueDocumentLine line) => new(line.Id, line.Speaker, line.Text);

    private readonly record struct Position(int LineId, string? State);

    private readonly record struct ChoiceKey(int SourceLineId, int Position)
    {
        public static ChoiceKey From(DialogueDocumentChoice choice) => new(choice.SourceLineId, choice.Position);
    }

    private sealed record Visit(int Distance, Position? Previous = null, DialogueDocumentChoice? Answer = null)
    {
        public List<Position> Incoming { get; } = [];
    }

    private sealed class Exploration(Dictionary<int, DialogueDocumentLine> lines, int? startLineId)
    {
        public Dictionary<int, DialogueDocumentLine> Lines { get; } = lines;
        public int? StartLineId { get; } = startLineId;
        public Dictionary<Position, Visit> Visits { get; } = [];
        public Dictionary<int, Position> FirstEndingPositions { get; } = [];
        public HashSet<ChoiceKey> AvailableChoices { get; } = [];
        public bool Complete { get; set; }
    }
}
