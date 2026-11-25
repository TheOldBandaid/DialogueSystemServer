using System.ComponentModel.DataAnnotations;
using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public sealed record DialogueDocument(
    [Range(1, 2)] int Version,
    [Required, StringLength(160)] string Title,
    [Range(1, int.MaxValue)] int? StartLineId,
    [Required] IReadOnlyList<DialogueDocumentLine> Lines,
    [Required] IReadOnlyList<DialogueDocumentChoice> Choices,
    [Range(1, int.MaxValue)] int? MinimumPlayerLevel = null) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Lines is null || Choices is null)
        {
            yield break;
        }

        if (Lines.Count > DialogueLimits.MaximumLines || Choices.Count > DialogueLimits.MaximumChoices)
        {
            yield return new($"Допускается до {DialogueLimits.MaximumLines} реплик и {DialogueLimits.MaximumChoices} переходов.");
            yield break;
        }

        var linesById = new Dictionary<int, DialogueDocumentLine>();
        var speakerNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines)
        {
            if (line is null || !linesById.TryAdd(line.Id, line))
            {
                yield return new("У каждой реплики должен быть свой идентификатор.", [nameof(Lines)]);
                yield break;
            }

            if (line.IsEnding && line.IsStoryBranch)
            {
                yield return new($"Концовка {line.Id} не может быть сюжетной развилкой.", [nameof(Lines)]);
            }

            if (Version == 2)
            {
                if (string.IsNullOrEmpty(line.SpeakerKey))
                    yield return new("В документе версии 2 у каждого говорящего должен быть ключ.", [nameof(Lines)]);
                else if (speakerNames.TryGetValue(line.SpeakerKey, out var name) && name != line.Speaker.Trim())
                    yield return new("Один ключ персонажа не может обозначать разные имена.", [nameof(Lines)]);
                else speakerNames[line.SpeakerKey] = line.Speaker.Trim();
            }
        }

        if (StartLineId is int startId && !linesById.ContainsKey(startId))
        {
            yield return new("Стартовая реплика должна быть в этом диалоге.", [nameof(StartLineId)]);
        }

        var positions = new HashSet<(int SourceLineId, int Position)>();
        foreach (var choice in Choices)
        {
            if (choice is null || !linesById.TryGetValue(choice.SourceLineId, out var source)
                || !linesById.ContainsKey(choice.TargetLineId))
            {
                yield return new("Переход должен связывать реплики из этого диалога.", [nameof(Choices)]);
                continue;
            }

            if (source.IsEnding)
            {
                yield return new($"У концовки {source.Id} не может быть исходящего перехода.", [nameof(Choices)]);
            }

            if (!positions.Add((choice.SourceLineId, choice.Position)))
            {
                yield return new($"У реплики {source.Id} несколько вариантов на позиции {choice.Position}.", [nameof(Choices)]);
            }
        }
    }
}

public sealed record DialogueDocumentLine(
    [Range(1, int.MaxValue)] int Id,
    [Required, StringLength(120)] string Speaker,
    [Required, StringLength(4000)] string Text,
    bool IsEnding,
    bool IsStoryBranch,
    [RegularExpression("[a-f0-9]{32}")] string? SpeakerKey = null);

public sealed record DialogueDocumentChoice(
    [Range(1, int.MaxValue)] int SourceLineId,
    [Range(1, int.MaxValue)] int TargetLineId,
    [Required, StringLength(500)] string Text,
    [Range(1, int.MaxValue)] int Position,
    [StringLength(DialogueChoice.MaximumStoryStateLength)] string? RequiredState = null,
    [StringLength(DialogueChoice.MaximumStoryStateLength)] string? NextState = null);
