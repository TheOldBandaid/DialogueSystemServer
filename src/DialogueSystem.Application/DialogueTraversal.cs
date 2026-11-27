namespace DialogueSystem.Application;

internal sealed class DialogueTraversal(DialogueGraph graph)
{
    private readonly Dictionary<StoryPosition, List<StoryPosition>> incoming = [];
    public IEnumerable<StoryPosition> Positions => incoming.Keys;
    public bool Complete { get; private set; }
    private int examined;

    public static DialogueTraversal Explore(DialogueGraph graph, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = graph.StartLineId is int id && graph.ContainsLine(id) ? id : (int?)null;
        var result = new DialogueTraversal(graph);
        if (start.HasValue) result.ContinueFrom(new(start.Value, null), ct);
        return result;
    }

    public void ContinueFrom(StoryPosition start, CancellationToken ct)
    {
        Complete = false;
        if (!graph.ContainsLine(start.LineId) || incoming.Count >= DialogueLimits.MaximumPositions) return;
        var pending = new Queue<StoryPosition>();
        Add(start);
        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            if (graph.IsEnding(position.LineId)) continue;
            foreach (var choice in graph.Outgoing(position.LineId))
            {
                ct.ThrowIfCancellationRequested();
                if (++examined > DialogueLimits.MaximumExaminedChoices) return;
                if (!DialogueStoryRules.Allows(choice.RequiredState, position.State) || !graph.ContainsLine(choice.TargetLineId)) continue;
                var next = new StoryPosition(choice.TargetLineId, DialogueStoryRules.Advance(choice.NextState, position.State));
                if (!incoming.ContainsKey(next))
                {
                    if (incoming.Count >= DialogueLimits.MaximumPositions) return;
                    Add(next);
                }
                incoming[next].Add(position);
            }
        }
        Complete = true;

        void Add(StoryPosition position)
        {
            incoming.Add(position, []);
            pending.Enqueue(position);
        }
    }

    public HashSet<StoryPosition> CanReachEnding(CancellationToken ct)
    {
        var result = Positions.Where(position => graph.IsEnding(position.LineId)).ToHashSet();
        var pending = new Queue<StoryPosition>(result);
        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var previous in incoming[position])
                if (result.Add(previous)) pending.Enqueue(previous);
        }
        return result;
    }
}
