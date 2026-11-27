using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace DialogueSystem.Application;

public sealed record CleanupCriteria(
    [property: JsonRequired] long BeforeUnixMilliseconds,
    [Range(1, 1000)] int KeepLatest = 20,
    bool Previews = true, bool Revisions = false, bool AllUsers = false,
    bool IncludeInterruptedPreviews = false);

public sealed record CleanupPlan(int PreviewCount, int RevisionCount, long HistoryStepCount,
    bool HasMore, string ConfirmationToken);
public sealed record CleanupSummary(int PreviewCount, int RevisionCount, long HistoryStepCount, bool HasMore);
public enum CleanupStatus { Ready, NotFound, Forbidden, Invalid, Stale }
public sealed record CleanupOutcome(CleanupStatus Status, CleanupPlan? Plan = null, CleanupSummary? Summary = null, string? Detail = null);

public interface IDialogueCleanupStore
{
    Task<CleanupOutcome> PlanAsync(int dialogueId, int userId, CleanupCriteria criteria, CancellationToken ct);
    Task<CleanupOutcome> ApplyAsync(int dialogueId, int userId, CleanupCriteria criteria, string confirmationToken, CancellationToken ct);
}

public sealed class DialogueCleanupService(IDialogueCleanupStore store, TimeProvider clock)
{
    public Task<CleanupOutcome> PlanAsync(int dialogueId, int userId, CleanupCriteria criteria, CancellationToken ct) =>
        Validate(criteria) is { } invalid ? Task.FromResult(invalid) : store.PlanAsync(dialogueId, userId, criteria, ct);

    public Task<CleanupOutcome> ApplyAsync(int dialogueId, int userId, CleanupCriteria criteria, string token, CancellationToken ct) =>
        Validate(criteria) is { } invalid ? Task.FromResult(invalid) : store.ApplyAsync(dialogueId, userId, criteria, token, ct);

    private CleanupOutcome? Validate(CleanupCriteria criteria)
    {
        if (criteria.BeforeUnixMilliseconds < 0 || criteria.BeforeUnixMilliseconds > clock.GetUtcNow().ToUnixTimeMilliseconds())
            return new(CleanupStatus.Invalid, Detail: "Укажите прошедшую дату очистки.");
        if (criteria.KeepLatest is < 1 or > 1000 || !criteria.Previews && !criteria.Revisions)
            return new(CleanupStatus.Invalid, Detail: "Выберите данные для очистки и сохраните от 1 до 1000 последних записей.");
        if (criteria.IncludeInterruptedPreviews && !criteria.Previews)
            return new(CleanupStatus.Invalid, Detail: "Для очистки прерванных прохождений включите категорию прохождений.");
        return null;
    }
}
