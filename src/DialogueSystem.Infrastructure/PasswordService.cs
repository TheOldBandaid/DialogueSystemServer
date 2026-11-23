using DialogueSystem.Application;
using DialogueSystem.Domain;
using Microsoft.AspNetCore.Identity;

namespace DialogueSystem.Infrastructure;

internal sealed class PasswordService : IPasswordService
{
    private readonly PasswordHasher<Account> hasher = new();

    public string Hash(Account account, string password) => hasher.HashPassword(account, password);

    public bool Verify(Account account, string password) =>
        hasher.VerifyHashedPassword(account, account.PasswordHash, password) != PasswordVerificationResult.Failed;
}
