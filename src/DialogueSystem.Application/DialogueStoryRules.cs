namespace DialogueSystem.Application;

public static class DialogueStoryRules
{
    public static bool Allows(string? required, string? current) => required is null || required == current;

    public static string? Advance(string? next, string? current) => next ?? current;
}
