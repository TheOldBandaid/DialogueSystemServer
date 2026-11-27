using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.EntityFrameworkCore;

namespace DialogueSystem.Infrastructure.Data;

internal sealed class SqliteAccounts(DialogueDbContext db, IPasswordService passwords) : IAccountService
{
    private Task<Account?> FindByLoginAsync(string login, CancellationToken ct) =>
        db.Accounts.AsNoTracking().SingleOrDefaultAsync(account => account.Login == login, ct);

    private Task<Account?> FindByIdAsync(int id, CancellationToken ct) =>
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

    private async Task AddAsync(Account account, CancellationToken ct)
    {
        db.Accounts.Add(account);
        await db.SaveChangesAsync(ct);
    }

    private Task<bool> HasOtherAdministratorAsync(int id, CancellationToken ct) =>
        db.Accounts.AnyAsync(account => account.Id != id && account.Role == AccountRoles.Administrator, ct);

    private Task SetRoleAsync(int id, string role, CancellationToken ct) =>
        db.Accounts.Where(account => account.Id == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(account => account.Role, role), ct);

    private async Task<bool> ChangePasswordAsync(int id, int version, string hash, CancellationToken ct, bool mustChange = false)
    {
        var updated = await db.Accounts.Where(account => account.Id == id && account.SessionVersion == version)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(account => account.PasswordHash, hash)
                .SetProperty(account => account.MustChangePassword, mustChange)
                .SetProperty(account => account.SessionVersion, account => account.SessionVersion + 1), ct);
        return updated != 0;
    }
    public async Task<AccountSession?> AuthenticateAsync(string login, string password, CancellationToken ct)
    {
        var account = await FindByLoginAsync(AccountRules.NormalizeLogin(login), ct);
        if (account is null || !passwords.Verify(account, password))
            return null;

        return new(account.Id, account.Login, account.Role, account.SessionVersion, account.MustChangePassword);
    }

    public async Task<bool> EnsureAsync(string login, string password, string role, CancellationToken ct)
    {
        if (await FindByLoginAsync(AccountRules.NormalizeLogin(login), ct) is not null)
            return false;
        return (await CreateAccountAsync(null, login, password, role, ct)).Status == AccountCreationStatus.Created;
    }

    public Task<AccountCreation> CreateAsync(AccountSession administrator, string login, string password, string role, CancellationToken ct) =>
        CreateAccountAsync(administrator, login, password, role, ct);

    private async Task<AccountCreation> CreateAccountAsync(AccountSession? administrator, string login, string password, string role, CancellationToken ct)
    {
        var normalized = AccountRules.NormalizeLogin(login);
        AccountRules.Validate(normalized, password, role);
        var account = new Account { Login = normalized, Role = role, PasswordHash = "" };
        account.PasswordHash = passwords.Hash(account, password);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (administrator is not null && !await IsCurrentAdministratorAsync(administrator, ct))
            return new(AccountCreationStatus.Forbidden);
        if (await FindByLoginAsync(account.Login, ct) is not null)
            return new(AccountCreationStatus.DuplicateLogin);

        await AddAsync(account, ct);
        await transaction.CommitAsync(ct);
        return new(AccountCreationStatus.Created, new(account.Id, account.Login, account.Role));
    }

    public async Task<RoleChange> ChangeRoleAsync(AccountSession administrator, int id, string role, string version, CancellationToken ct)
    {
        AccountRules.ValidateRole(role);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await IsCurrentAdministratorAsync(administrator, ct)) return RoleChange.Forbidden;
        var account = await GetSessionAsync(id, ct);
        if (account is null)
            return RoleChange.NotFound;
        if (version != account.Summary.Version) return RoleChange.StaleAccount;

        if (account.Role == AccountRoles.Administrator && role == AccountRoles.Editor &&
            !await HasOtherAdministratorAsync(id, ct))
            return RoleChange.LastAdministrator;

        await SetRoleAsync(id, role, ct);
        await transaction.CommitAsync(ct);
        return RoleChange.Changed;
    }

    private async Task<bool> IsCurrentAdministratorAsync(AccountSession administrator, CancellationToken ct)
    {
        var current = await GetSessionAsync(administrator.Id, ct);
        return administrator.Role == AccountRoles.Administrator && !administrator.MustChangePassword &&
            current is { Role: AccountRoles.Administrator, MustChangePassword: false } &&
            current.Version == administrator.Version;
    }

    public async Task<PasswordChange> ChangePasswordAsync(
        int id, string currentPassword, string newPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(newPassword);
        var account = await FindByIdAsync(id, ct);
        if (account is null || !passwords.Verify(account, currentPassword))
            return PasswordChange.WrongPassword;

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            return PasswordChange.SamePassword;

        var hash = passwords.Hash(account, newPassword);
        return await ChangePasswordAsync(id, account.SessionVersion, hash, ct)
            ? PasswordChange.Changed
            : PasswordChange.StaleAccount;
    }

    public async Task<PasswordReset> ResetPasswordAsync(int administratorId, int targetId,
        string currentPassword, string temporaryPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(temporaryPassword);
        var administrator = await FindByIdAsync(administratorId, ct);
        if (administrator is null || administrator.Role != AccountRoles.Administrator || administrator.MustChangePassword)
            return PasswordReset.Forbidden;
        if (!passwords.Verify(administrator, currentPassword)) return PasswordReset.WrongPassword;
        if (administratorId == targetId) return PasswordReset.OwnAccount;
        var account = await FindByIdAsync(targetId, ct);
        if (account is null) return PasswordReset.NotFound;
        var hash = passwords.Hash(account, temporaryPassword);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var currentAdministrator = await GetSessionAsync(administratorId, ct);
        if (currentAdministrator is null || currentAdministrator.Role != AccountRoles.Administrator ||
            currentAdministrator.MustChangePassword || currentAdministrator.Version != administrator.SessionVersion)
            return PasswordReset.Forbidden;
        if (!await ChangePasswordAsync(targetId, account.SessionVersion, hash, ct, mustChange: true))
            return PasswordReset.StaleAccount;
        await transaction.CommitAsync(ct);
        return PasswordReset.Changed;
    }

    public async Task<bool> RecoverAdministratorAsync(string login, string newPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(newPassword);
        var account = await FindByLoginAsync(AccountRules.NormalizeLogin(login), ct);
        if (account is null) return false;
        var hash = passwords.Hash(account, newPassword);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        if (!await ChangePasswordAsync(account.Id, account.SessionVersion, hash, ct)) return false;
        await SetRoleAsync(account.Id, AccountRoles.Administrator, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
