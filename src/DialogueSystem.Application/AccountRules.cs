using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;

namespace DialogueSystem.Application;

public static partial class AccountRules
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 512;

    public static string NormalizeLogin(string login) => login.Trim().ToLowerInvariant();

    public static void Validate(string login, string password, string role)
    {
        if (!LoginPattern().IsMatch(login))
            throw new ValidationException("Логин должен содержать от 3 до 100 латинских букв, цифр, точек, дефисов или подчёркиваний.");
        ValidatePassword(password);
        ValidateRole(role);
    }

    public static void ValidatePassword(string password)
    {
        if (password.Length is < MinimumPasswordLength or > MaximumPasswordLength)
            throw new ValidationException("Пароль должен содержать от 12 до 512 символов.");
    }

    public static void ValidateRole(string role)
    {
        if (role is not (AccountRoles.Administrator or AccountRoles.Editor))
            throw new ValidationException("Неизвестная роль пользователя.");
    }

    [GeneratedRegex("\\A[A-Za-z0-9][A-Za-z0-9._-]{2,99}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LoginPattern();
}
