namespace DialogueSystem.Domain;

public sealed class DialogueRevision
{
    public int Id { get; set; }
    public int DialogueId { get; set; }
    public required string Label { get; set; }
    public required string Author { get; set; }
    public long CreatedAtUnixMilliseconds { get; set; }
    public int LineCount { get; set; }
    public int ChoiceCount { get; set; }
    public required string DocumentJson { get; set; }
}
