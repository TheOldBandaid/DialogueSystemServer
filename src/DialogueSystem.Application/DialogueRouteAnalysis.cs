namespace DialogueSystem.Application;

public static class DialogueRouteAnalysis
{
    private const int MaximumExamples = 50;

    public static DialogueAnalysisReport Analyze(DialogueDocument document, CancellationToken ct = default) =>
        Analyze(new DialogueGraph(document), ct);

    public static DialogueRouteResult FindShortestRoute(DialogueDocument document, int endingId, CancellationToken ct = default) =>
        FindShortestRoute(new DialogueGraph(document), endingId, ct);

    public static DialogueAnalysisReport Analyze(DialogueGraph graph, CancellationToken ct = default)
    {
        var walk = DialogueTraversal.Explore(graph, ct);
        var reachableLines = walk.Visits.Keys.Select(position => position.LineId).ToHashSet();
        var endings = graph.Lines.Values.Where(line => line.IsEnding).OrderBy(line => line.Id)
            .Select(line => DescribeEnding(walk, line)).ToArray();

        var unreachable = walk.Complete
            ? graph.Lines.Values.Where(line => !reachableLines.Contains(line.Id)).OrderBy(line => line.Id).ToArray()
            : [];
        var unavailable = walk.Complete
            ? graph.Choices.Where(choice => !walk.AvailableChoices.Contains(GraphChoiceKey.From(choice)))
                .OrderBy(choice => choice.SourceLineId).ThenBy(choice => choice.Position).ToArray()
            : [];
        var trapped = walk.Complete
            ? FindTrappedPositions(walk, ct)
            : [];

        return new DialogueAnalysisReport(graph.Title, walk.StartLineId, graph.MinimumPlayerLevel, walk.Complete,
            graph.Lines.Count, reachableLines.Count, walk.Visits.Count, graph.Choices.Count,
            walk.AvailableChoices.Count, walk.Complete ? unreachable.Length : null,
            walk.Complete ? unavailable.Length : null, walk.Complete ? trapped.Length : null, endings,
            unreachable.Take(MaximumExamples).Select(Reference).ToArray(),
            unavailable.Take(MaximumExamples).Select(choice => ExplainUnavailable(choice, walk, reachableLines)).ToArray(),
            trapped.Take(MaximumExamples).Select(position => new TrappedDialoguePosition(position.LineId, position.State))
                .ToArray());
    }

    public static DialogueRouteResult FindShortestRoute(
        DialogueGraph graph, int endingId, CancellationToken ct = default)
    {
        var walk = DialogueTraversal.Explore(graph, ct);
        if (!walk.FirstEndingPositions.TryGetValue(endingId, out var ending))
            return new(walk.Complete, null);

        var steps = new List<DialogueRouteStep>();
        var position = ending;
        while (walk.Visits[position] is { Previous: StoryPosition previous, Answer: { } answer })
        {
            steps.Add(new(previous.LineId, position.LineId, answer.Position, answer.Text, answer.RequiredState,
                previous.State, position.State));
            position = previous;
        }

        steps.Reverse();
        return new(walk.Complete, new(position.LineId, ending.LineId, ending.State, steps));
    }

    private static DialogueEndingAnalysis DescribeEnding(DialogueTraversal walk, DialogueGraphLine line) =>
        walk.FirstEndingPositions.TryGetValue(line.Id, out var ending)
            ? new(Reference(line), walk.Visits[ending].Distance, true)
            : new(Reference(line), null, walk.Complete ? false : null);

    private static StoryPosition[] FindTrappedPositions(DialogueTraversal walk, CancellationToken ct) =>
        walk.Visits.Keys.Except(walk.CanReachEnding(ct))
            .OrderBy(position => position.LineId).ThenBy(position => position.State, StringComparer.Ordinal).ToArray();

    private static UnavailableDialogueChoice ExplainUnavailable(
        DialogueGraphChoice choice, DialogueTraversal walk, HashSet<int> reachableLines)
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

    private static DialogueLineReference Reference(DialogueGraphLine line) => new(line.Id, line.Speaker, line.Text);

}
