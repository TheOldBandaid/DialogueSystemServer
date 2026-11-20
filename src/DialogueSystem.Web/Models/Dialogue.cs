namespace DialogueSystem.Web.Models;

public sealed class Character
{
    public int Id { get; set; }
    public required string Name { get; set; }
}

public sealed class Dialogue
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public int? StartLineId { get; set; }
    public List<DialogueLine> Lines { get; } = [];
}

public sealed class DialogueLine
{
    public int Id { get; set; }
    public int DialogueId { get; set; }
    public int SpeakerId { get; set; }
    public Character Speaker { get; set; } = null!;
    public required string Text { get; set; }
    public bool IsEnding { get; set; }
    public bool IsStoryBranch { get; set; }
    public List<DialogueChoice> Choices { get; } = [];
}

public sealed class DialogueChoice
{
    public int Id { get; set; }
    public int DialogueId { get; set; }
    public int SourceLineId { get; set; }
    public int TargetLineId { get; set; }
    public required string Text { get; set; }
    public int Position { get; set; }
}

public sealed class Account
{
    public int Id { get; set; }
    public required string Login { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; }
}
