using System.Security.Cryptography;
using System.Text.Json;
using DialogueSystem.Application;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteCleanup(DialogueDbContext db, DatabaseBackups backups, TimeProvider clock) : IDialogueCleanup
{
    private const int BatchSize = 100;
    private sealed record PreviewCandidate(Guid Id, int Step, long HistoryCount);
    private sealed record RevisionCandidate(int Id, long CreatedAt, string Label);
    private sealed record Selection(CleanupOutcome Outcome, IReadOnlyList<PreviewCandidate> Previews, IReadOnlyList<RevisionCandidate> Revisions);

    public Task<CleanupOutcome> PlanAsync(int dialogueId, int userId, CleanupCriteria criteria, CancellationToken ct) =>
        Validate(criteria) is { } invalid ? Task.FromResult(invalid) :
            SqliteReadSnapshot.ReadAsync(db, async () => (await SelectAsync(dialogueId, userId, criteria, ct)).Outcome, ct);

    public async Task<CleanupOutcome> ApplyAsync(int dialogueId, int userId, CleanupCriteria criteria, string confirmationToken, CancellationToken ct)
    {
        var planned = await PlanAsync(dialogueId, userId, criteria, ct);
        if (planned.Status != CleanupStatus.Ready) return planned;
        var plan = planned.Plan!;
        if (!string.Equals(confirmationToken, plan.ConfirmationToken, StringComparison.Ordinal))
            return new(CleanupStatus.Stale, Detail: "Состав данных изменился. Снова рассчитайте очистку перед подтверждением.");
        if (plan.PreviewCount + plan.RevisionCount == 0)
            return new(CleanupStatus.Ready, Summary: new(0, 0, 0, false));

        await backups.CreateAsync(BackupKind.Cleanup, ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var selection = await SelectAsync(dialogueId, userId, criteria, ct);
        if (selection.Outcome.Status != CleanupStatus.Ready) return selection.Outcome;
        if (!string.Equals(confirmationToken, selection.Outcome.Plan!.ConfirmationToken, StringComparison.Ordinal))
            return new(CleanupStatus.Stale, Detail: "Данные изменились за время копирования. Снова рассчитайте очистку перед подтверждением.");

        var previewIds = selection.Previews.Select(preview => preview.Id).ToArray();
        var revisionIds = selection.Revisions.Select(revision => revision.Id).ToArray();
        var removedPreviews = previewIds.Length == 0 ? 0 : await db.Previews.Where(preview => previewIds.Contains(preview.Id)).ExecuteDeleteAsync(ct);
        var removedRevisions = revisionIds.Length == 0 ? 0 : await db.DialogueRevisions.Where(revision => revisionIds.Contains(revision.Id)).ExecuteDeleteAsync(ct);
        if (removedPreviews != plan.PreviewCount || removedRevisions != plan.RevisionCount)
            throw new DbUpdateConcurrencyException("Состав очистки изменился внутри транзакции.");
        await transaction.CommitAsync(ct);
        return new(CleanupStatus.Ready, Summary: new(removedPreviews, removedRevisions, plan.HistoryStepCount, plan.HasMore));
    }

    private async Task<Selection> SelectAsync(int dialogueId, int userId, CleanupCriteria criteria, CancellationToken ct)
    {
        var actor = await db.Accounts.Where(account => account.Id == userId)
            .Select(account => new { account.Login, account.Role, account.MustChangePassword }).SingleOrDefaultAsync(ct);
        if (actor is null || actor.MustChangePassword || actor.Role is not (AccountRoles.Editor or AccountRoles.Administrator) ||
            criteria.AllUsers && actor.Role != AccountRoles.Administrator)
            return new(new(CleanupStatus.Forbidden), [], []);
        var activityRevision = await db.Dialogues.Where(dialogue => dialogue.Id == dialogueId)
            .Select(dialogue => (long?)dialogue.ActivityRevision).SingleOrDefaultAsync(ct);
        if (activityRevision is null) return new(new(CleanupStatus.NotFound), [], []);
        var previews = new List<PreviewCandidate>();
        var revisions = new List<RevisionCandidate>();
        if (criteria.Previews)
        {
            var scope = db.Previews.Where(preview => preview.DialogueId == dialogueId && (criteria.AllUsers || preview.UserId == userId));
            var protectedIds = scope.OrderByDescending(preview => preview.StartedAtUnixMilliseconds).ThenByDescending(preview => preview.Id)
                .Take(criteria.KeepLatest).Select(preview => preview.Id);
            previews = await scope.Where(preview => preview.StartedAtUnixMilliseconds != null &&
                    preview.StartedAtUnixMilliseconds < criteria.BeforeUnixMilliseconds && !protectedIds.Contains(preview.Id) &&
                    (preview.History.Any(entry => entry.Step == preview.Step && entry.IsEnding) ||
                     criteria.IncludeInterruptedPreviews && !db.Lines.Any(line => line.DialogueId == preview.DialogueId && line.Id == preview.CurrentLineId)))
                .OrderBy(preview => preview.StartedAtUnixMilliseconds).ThenBy(preview => preview.Id)
                .Select(preview => new PreviewCandidate(preview.Id, preview.Step, preview.History.LongCount()))
                .Take(BatchSize + 1).ToListAsync(ct);
        }
        if (criteria.Revisions)
        {
            var scope = db.DialogueRevisions.Where(revision => revision.DialogueId == dialogueId &&
                (criteria.AllUsers || revision.Author == actor.Login));
            var protectedIds = scope.OrderByDescending(revision => revision.Id).Take(criteria.KeepLatest).Select(revision => revision.Id);
            revisions = await scope.Where(revision => revision.CreatedAtUnixMilliseconds < criteria.BeforeUnixMilliseconds && !protectedIds.Contains(revision.Id))
                .OrderBy(revision => revision.Id).Select(revision => new RevisionCandidate(revision.Id, revision.CreatedAtUnixMilliseconds, revision.Label))
                .Take(BatchSize + 1).ToListAsync(ct);
        }
        var hasMore = previews.Count > BatchSize || revisions.Count > BatchSize;
        if (previews.Count > BatchSize) previews.RemoveAt(BatchSize);
        if (revisions.Count > BatchSize) revisions.RemoveAt(BatchSize);
        var token = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { dialogueId, userId, criteria, activityRevision, previews, revisions })));
        var plan = new CleanupPlan(previews.Count, revisions.Count, previews.Sum(preview => preview.HistoryCount), hasMore, token);
        return new(new(CleanupStatus.Ready, Plan: plan), previews, revisions);
    }
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
