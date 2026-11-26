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
            .Select(account => new AccountSession(account.Id, account.Login, account.Role, account.SessionVersion))
            .SingleOrDefaultAsync(ct);

    public async Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken ct) =>
        await db.Accounts.OrderBy(account => account.Login)
            .Select(account => new AccountSummary(account.Id, account.Login, account.Role))
            .ToListAsync(ct);

    public async Task<InitialAccountsStatus> InitialStatusAsync(CancellationToken ct)
    {
        var accounts = await db.Accounts.Select(account => new { account.Login, account.Role }).ToListAsync(ct);
        return new(accounts.Any(account => account.Login == "admin"),
            accounts.Any(account => account.Login == "editor"),
            accounts.Any(account => account.Role == AccountRoles.Administrator));
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

    public async Task<bool> ChangePasswordAsync(int id, int version, string hash, CancellationToken ct)
    {
        var updated = await db.Accounts.Where(account => account.Id == id && account.SessionVersion == version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(account => account.PasswordHash, hash)
                .SetProperty(account => account.SessionVersion, account => account.SessionVersion + 1), ct);
        return updated != 0;
    }
}
