using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public static class AccountRoles
{
    public const string Administrator = "Administrator";
    public const string Editor = "Editor";
    public const string Pattern = Administrator + "|" + Editor;
}

public sealed record AccountSummary(int Id, string Login, string Role, bool MustChangePassword = false)
{
    public string Version => EditVersions.AccountRole(Id, Role, MustChangePassword);
}

public sealed record AccountSession(int Id, string Login, string Role, int Version, bool MustChangePassword = false)
{
    public AccountSummary Summary => new(Id, Login, Role, MustChangePassword);
}

public enum RoleChange { Changed, NotFound, LastAdministrator, StaleAccount }
public enum PasswordChange { Changed, WrongPassword, SamePassword, StaleAccount }
public enum PasswordReset { Changed, NotFound, WrongPassword, Forbidden, OwnAccount, StaleAccount }
public sealed record InitialAccountsStatus(bool AdminExists, bool EditorExists, bool HasAdministrator);

public interface IPasswordService
{
    string Hash(Account account, string password);
    bool Verify(Account account, string password);
}

public interface IAccountStore
{
    Task<Account?> FindByLoginAsync(string login, CancellationToken ct);
    Task<Account?> FindByIdAsync(int id, CancellationToken ct);
    Task<AccountSession?> GetSessionAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken ct);
    Task<CatalogPage<AccountSummary>> PageAsync(string? query, int? before, CancellationToken ct);
    Task<InitialAccountsStatus> InitialStatusAsync(CancellationToken ct);
    Task<IStorageTransaction> BeginAsync(CancellationToken ct);
    Task AddAsync(Account account, CancellationToken ct);
    Task<bool> HasOtherAdministratorAsync(int id, CancellationToken ct);
    Task SetRoleAsync(int id, string role, CancellationToken ct);
    Task<bool> ChangePasswordAsync(int id, int version, string hash, CancellationToken ct, bool mustChange = false);
}

public sealed class AccountService(IAccountStore store, IPasswordService passwords)
{
    public async Task<AccountSession?> AuthenticateAsync(string login, string password, CancellationToken ct)
    {
        var account = await store.FindByLoginAsync(AccountRules.NormalizeLogin(login), ct);
        if (account is null || !passwords.Verify(account, password))
            return null;

        return new(account.Id, account.Login, account.Role, account.SessionVersion, account.MustChangePassword);
    }

    public Task<AccountSession?> GetSessionAsync(int id, CancellationToken ct) => store.GetSessionAsync(id, ct);

    public Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public Task<CatalogPage<AccountSummary>> PageAsync(string? query, int? before, CancellationToken ct) => store.PageAsync(query, before, ct);

    public Task<InitialAccountsStatus> InitialStatusAsync(CancellationToken ct) => store.InitialStatusAsync(ct);

    public async Task<bool> EnsureAsync(string login, string password, string role, CancellationToken ct)
    {
        if (await store.FindByLoginAsync(AccountRules.NormalizeLogin(login), ct) is not null)
            return false;
        return await CreateAsync(login, password, role, ct) is not null;
    }

    public async Task<AccountSummary?> CreateAsync(string login, string password, string role, CancellationToken ct)
    {
        var normalized = AccountRules.NormalizeLogin(login);
        AccountRules.Validate(normalized, password, role);
        var account = new Account { Login = normalized, Role = role, PasswordHash = "" };
        account.PasswordHash = passwords.Hash(account, password);

        await using var transaction = await store.BeginAsync(ct);
        if (await store.FindByLoginAsync(account.Login, ct) is not null)
            return null;

        await store.AddAsync(account, ct);
        await transaction.CommitAsync(ct);
        return new(account.Id, account.Login, account.Role);
    }

    public async Task<RoleChange> ChangeRoleAsync(int id, string role, string version, CancellationToken ct)
    {
        AccountRules.ValidateRole(role);
        await using var transaction = await store.BeginAsync(ct);
        var account = await store.GetSessionAsync(id, ct);
        if (account is null)
            return RoleChange.NotFound;
        if (version != account.Summary.Version) return RoleChange.StaleAccount;

        if (account.Role == AccountRoles.Administrator && role == AccountRoles.Editor &&
            !await store.HasOtherAdministratorAsync(id, ct))
            return RoleChange.LastAdministrator;

        await store.SetRoleAsync(id, role, ct);
        await transaction.CommitAsync(ct);
        return RoleChange.Changed;
    }

    public async Task<PasswordChange> ChangePasswordAsync(
        int id, string currentPassword, string newPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(newPassword);
        var account = await store.FindByIdAsync(id, ct);
        if (account is null || !passwords.Verify(account, currentPassword))
            return PasswordChange.WrongPassword;

        if (string.Equals(currentPassword, newPassword, StringComparison.Ordinal))
            return PasswordChange.SamePassword;

        var hash = passwords.Hash(account, newPassword);
        return await store.ChangePasswordAsync(id, account.SessionVersion, hash, ct)
            ? PasswordChange.Changed
            : PasswordChange.StaleAccount;
    }

    public async Task<PasswordReset> ResetPasswordAsync(int administratorId, int targetId,
        string currentPassword, string temporaryPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(temporaryPassword);
        var administrator = await store.FindByIdAsync(administratorId, ct);
        if (administrator is null || administrator.Role != AccountRoles.Administrator || administrator.MustChangePassword)
            return PasswordReset.Forbidden;
        if (!passwords.Verify(administrator, currentPassword)) return PasswordReset.WrongPassword;
        if (administratorId == targetId) return PasswordReset.OwnAccount;
        var account = await store.FindByIdAsync(targetId, ct);
        if (account is null) return PasswordReset.NotFound;
        var hash = passwords.Hash(account, temporaryPassword);
        await using var transaction = await store.BeginAsync(ct);
        var currentAdministrator = await store.GetSessionAsync(administratorId, ct);
        if (currentAdministrator is null || currentAdministrator.Role != AccountRoles.Administrator ||
            currentAdministrator.MustChangePassword || currentAdministrator.Version != administrator.SessionVersion)
            return PasswordReset.Forbidden;
        if (!await store.ChangePasswordAsync(targetId, account.SessionVersion, hash, ct, mustChange: true))
            return PasswordReset.StaleAccount;
        await transaction.CommitAsync(ct);
        return PasswordReset.Changed;
    }

    public async Task<bool> RecoverAdministratorAsync(string login, string newPassword, CancellationToken ct)
    {
        AccountRules.ValidatePassword(newPassword);
        var account = await store.FindByLoginAsync(AccountRules.NormalizeLogin(login), ct);
        if (account is null) return false;
        var hash = passwords.Hash(account, newPassword);
        await using var transaction = await store.BeginAsync(ct);
        if (!await store.ChangePasswordAsync(account.Id, account.SessionVersion, hash, ct)) return false;
        await store.SetRoleAsync(account.Id, AccountRoles.Administrator, ct);
        await transaction.CommitAsync(ct);
        return true;
    }
}
