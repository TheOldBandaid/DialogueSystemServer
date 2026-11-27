namespace DialogueSystem.Application;

internal sealed class DialogueTraversal(DialogueGraph graph, int? startLineId)
{
    internal sealed record Visit(int Distance, StoryPosition? Previous = null, DialogueGraphChoice? Answer = null)
    {
        public List<StoryPosition> Incoming { get; } = [];
    }

    public IReadOnlyDictionary<int, DialogueGraphLine> Lines => graph.Lines;
    public int? StartLineId { get; } = startLineId;
    public Dictionary<StoryPosition, Visit> Visits { get; } = [];
    public Dictionary<int, StoryPosition> FirstEndingPositions { get; } = [];
    public HashSet<GraphChoiceKey> AvailableChoices { get; } = [];
    public bool Complete { get; private set; }
    private int examined;

    public static DialogueTraversal Explore(DialogueGraph graph, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = graph.StartLineId is int id && graph.ContainsLine(id) ? id : (int?)null;
        var result = new DialogueTraversal(graph, start);
        if (start.HasValue) result.ContinueFrom(new(start.Value, null), ct);
        return result;
    }

    public void ContinueFrom(StoryPosition start, CancellationToken ct)
    {
        Complete = false;
        if (!graph.ContainsLine(start.LineId) || Visits.Count >= DialogueLimits.MaximumPositions) return;
        var pending = new Queue<StoryPosition>();
        Add(start, new(0));
        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            if (Lines[position.LineId].IsEnding) continue;
            var distance = Visits[position].Distance + 1;
            foreach (var choice in graph.Outgoing(position.LineId))
            {
                ct.ThrowIfCancellationRequested();
                if (++examined > DialogueLimits.MaximumExaminedChoices) return;
                if (!DialogueStoryRules.Allows(choice.RequiredState, position.State) || !graph.ContainsLine(choice.TargetLineId)) continue;
                var next = new StoryPosition(choice.TargetLineId, DialogueStoryRules.Advance(choice.NextState, position.State));
                if (!Visits.TryGetValue(next, out var visit))
                {
                    if (Visits.Count >= DialogueLimits.MaximumPositions) return;
                    visit = new(distance, position, choice);
                    Add(next, visit);
                }
                visit.Incoming.Add(position);
                AvailableChoices.Add(GraphChoiceKey.From(choice));
            }
        }
        Complete = true;

        void Add(StoryPosition position, Visit visit)
        {
            Visits.Add(position, visit);
            pending.Enqueue(position);
            if (Lines[position.LineId].IsEnding) FirstEndingPositions.TryAdd(position.LineId, position);
        }
    }

    public HashSet<StoryPosition> CanReachEnding(CancellationToken ct)
    {
        var result = Visits.Keys.Where(position => Lines[position.LineId].IsEnding).ToHashSet();
        var pending = new Queue<StoryPosition>(result);
        while (pending.TryDequeue(out var position))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var previous in Visits[position].Incoming)
                if (result.Add(previous)) pending.Enqueue(previous);
        }
        return result;
    }
}
