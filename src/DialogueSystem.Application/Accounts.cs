using DialogueSystem.Domain;

namespace DialogueSystem.Application;

public static class AccountRoles
{
    public const string Administrator = "Administrator";
    public const string Editor = "Editor";
    public const string Pattern = Administrator + "|" + Editor;
}

public sealed record AccountSummary(int Id, string Login, string Role);

public sealed record AccountSession(int Id, string Login, string Role, int Version)
{
    public AccountSummary Summary => new(Id, Login, Role);
}

public enum RoleChange { Changed, NotFound, LastAdministrator }
public enum PasswordChange { Changed, WrongPassword, SamePassword, StaleAccount }

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
    Task<IStorageTransaction> BeginAsync(CancellationToken ct);
    Task AddAsync(Account account, CancellationToken ct);
    Task<bool> HasOtherAdministratorAsync(int id, CancellationToken ct);
    Task SetRoleAsync(int id, string role, CancellationToken ct);
    Task<bool> ChangePasswordAsync(int id, int version, string hash, CancellationToken ct);
}

public sealed class AccountService(IAccountStore store, IPasswordService passwords)
{
    public async Task<AccountSession?> AuthenticateAsync(string login, string password, CancellationToken ct)
    {
        var account = await store.FindByLoginAsync(NormalizeLogin(login), ct);
        if (account is null || !passwords.Verify(account, password))
            return null;

        return new(account.Id, account.Login, account.Role, account.SessionVersion);
    }

    public Task<AccountSession?> GetSessionAsync(int id, CancellationToken ct) => store.GetSessionAsync(id, ct);

    public Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken ct) => store.ListAsync(ct);

    public async Task<AccountSummary?> CreateAsync(string login, string password, string role, CancellationToken ct)
    {
        var account = new Account { Login = NormalizeLogin(login), Role = role, PasswordHash = "" };
        account.PasswordHash = passwords.Hash(account, password);

        await using var transaction = await store.BeginAsync(ct);
        if (await store.FindByLoginAsync(account.Login, ct) is not null)
            return null;

        await store.AddAsync(account, ct);
        await transaction.CommitAsync(ct);
        return new(account.Id, account.Login, account.Role);
    }

    public async Task<RoleChange> ChangeRoleAsync(int id, string role, CancellationToken ct)
    {
        await using var transaction = await store.BeginAsync(ct);
        var account = await store.GetSessionAsync(id, ct);
        if (account is null)
            return RoleChange.NotFound;

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

    private static string NormalizeLogin(string login) => login.Trim().ToLowerInvariant();
}
