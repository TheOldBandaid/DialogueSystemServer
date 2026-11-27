namespace DialogueSystem.Application;

public static class DialogueLimits
{
    public const int MaximumLines = 5_000;
    public const int MaximumChoices = 20_000;
    public const long MaximumImportBytes = 256L * 1024 * 1024;
    public const int MaximumPositions = 10_000;
    public const int MaximumExaminedChoices = 100_000;
    public const int MaximumValidationIssues = 100;
}
