using DialogueSystem.Application;

namespace DialogueSystem.Tests;

internal sealed record DialogueSettings(string Title, int? StartLineId, int? MinimumPlayerLevel, string? Version)
{
    public static DialogueSettings From(DialogueSummary current) =>
        new(current.Title, current.StartLineId, current.MinimumPlayerLevel, current.Version);
}
