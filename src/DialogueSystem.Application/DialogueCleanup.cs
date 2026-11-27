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

public interface IDialogueCleanup
{
    Task<CleanupOutcome> PlanAsync(int dialogueId, int userId, CleanupCriteria criteria, CancellationToken ct);
    Task<CleanupOutcome> ApplyAsync(int dialogueId, int userId, CleanupCriteria criteria, string confirmationToken, CancellationToken ct);
}
