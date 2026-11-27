namespace DialogueSystem.Application;

public static class CatalogLimits
{
    public const int PageSize = 20;
    public const int SearchLength = 160;
    public const int LinePreviewLength = 240;
}

public sealed record CatalogPage<T>(IReadOnlyList<T> Items, int? NextBeforeId);
public sealed record LineReference(int Id, string Speaker, string Text, bool IsEnding, bool IsStoryBranch, int SpeakerId);
