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

public enum RoleChange { Changed, NotFound, LastAdministrator, StaleAccount, Forbidden }
public enum AccountCreationStatus { Created, DuplicateLogin, Forbidden }
public sealed record AccountCreation(AccountCreationStatus Status, AccountSummary? Account = null);
public enum PasswordChange { Changed, WrongPassword, SamePassword, StaleAccount }
public enum PasswordReset { Changed, NotFound, WrongPassword, Forbidden, OwnAccount, StaleAccount }
public sealed record InitialAccountsStatus(bool AdminExists, bool EditorExists, bool HasAdministrator);

public interface IPasswordService
{
    string Hash(Account account, string password);
    bool Verify(Account account, string password);
}

public interface IAccountService
{
    Task<AccountSession?> AuthenticateAsync(string login, string password, CancellationToken ct);
    Task<AccountSession?> GetSessionAsync(int id, CancellationToken ct);
    Task<CatalogPage<AccountSummary>> PageAsync(string? query, int? before, CancellationToken ct);
    Task<InitialAccountsStatus> InitialStatusAsync(CancellationToken ct);
    Task<bool> EnsureAsync(string login, string password, string role, CancellationToken ct);
    Task<AccountCreation> CreateAsync(AccountSession administrator, string login, string password, string role, CancellationToken ct);
    Task<RoleChange> ChangeRoleAsync(AccountSession administrator, int id, string role, string version, CancellationToken ct);
    Task<PasswordChange> ChangePasswordAsync(int id, string currentPassword, string newPassword, CancellationToken ct);
    Task<PasswordReset> ResetPasswordAsync(int administratorId, int targetId, string currentPassword, string temporaryPassword, CancellationToken ct);
    Task<bool> RecoverAdministratorAsync(string login, string newPassword, CancellationToken ct);
}
