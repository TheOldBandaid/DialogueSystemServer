using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteAccounts(DialogueDbContext db) : IAccountStore
{
    public Task<Account?> FindByLoginAsync(string login, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Login == login, ct);

    public Task<Account?> FindByIdAsync(int id, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == id, ct);

    public Task<AccountSession?> GetSessionAsync(int id, CancellationToken ct) =>
        db.Accounts.Where(account => account.Id == id)
            .Select(account => new AccountSession(account.Id, account.Login, account.Role, account.SessionVersion, account.MustChangePassword))
            .SingleOrDefaultAsync(ct);

    public Task<CatalogPage<AccountSummary>> PageAsync(string? query, int? before, CancellationToken ct)
    {
        var selected = db.Accounts.AsQueryable();
        var text = query?.Trim();
        if (!string.IsNullOrEmpty(text)) selected = selected.Where(account => SqliteSearch.Contains(account.Login, text));
        if (before.HasValue) selected = selected.Where(account => account.Id < before.Value);
        return CatalogQueries.PageAsync(selected.OrderByDescending(account => account.Id)
            .Select(account => new AccountSummary(account.Id, account.Login, account.Role, account.MustChangePassword)), account => account.Id, ct);
    }

    public async Task<InitialAccountsStatus> InitialStatusAsync(CancellationToken ct)
    {
        return new(await db.Accounts.AnyAsync(account => account.Login == "admin", ct),
            await db.Accounts.AnyAsync(account => account.Login == "editor", ct),
            await db.Accounts.AnyAsync(account => account.Role == AccountRoles.Administrator, ct));
    }

    public async Task<IStorageTransaction> BeginAsync(CancellationToken ct) =>
        new SqliteTransaction(await db.Database.BeginTransactionAsync(ct));

    public async Task AddAsync(Account account, CancellationToken ct)
    {
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    public Task<bool> HasOtherAdministratorAsync(int id, CancellationToken ct) =>
        db.Accounts.AnyAsync(account => account.Id != id && account.Role == AccountRoles.Administrator, ct);

    public Task SetRoleAsync(int id, string role, CancellationToken ct) =>
        db.Accounts.Where(account => account.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Role, role), ct);

    public async Task<bool> ChangePasswordAsync(int id, int version, string hash, CancellationToken ct, bool mustChange = false)
    {
        var updated = await db.Accounts.Where(account => account.Id == id && account.SessionVersion == version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(account => account.PasswordHash, hash)
                .SetProperty(account => account.MustChangePassword, mustChange)
                .SetProperty(account => account.SessionVersion, account => account.SessionVersion + 1), ct);
        return updated != 0;
    }
}
