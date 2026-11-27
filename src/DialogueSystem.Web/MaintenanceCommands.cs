using System.Text.Json;
using DialogueSystem.Application;

namespace DialogueSystem.Web;

internal static class MaintenanceCommands
{
    public static async Task<int> RunAsync(string command, IServiceProvider services, IConfiguration configuration)
    {
        var accounts = services.GetRequiredService<AccountService>();
        switch (command)
        {
            case "accounts-status":
                Console.WriteLine(JsonSerializer.Serialize(await accounts.InitialStatusAsync(CancellationToken.None),
                    JsonSerializerOptions.Web));
                return 0;
            case "reset-admin":
                var login = configuration["login"] ?? throw new ArgumentException("Укажите --login существующего пользователя.");
                var password = ReadNewPassword();
                if (!await accounts.RecoverAdministratorAsync(login, password, CancellationToken.None))
                {
                    Console.Error.WriteLine("Пользователь не найден или уже изменился. Пароль не изменён.");
                    return 1;
                }
                Console.WriteLine("Доступ администратора восстановлен. Прежние сеансы этого пользователя завершены.");
                return 0;
            default:
                Console.Error.WriteLine("Неизвестная команда обслуживания.");
                return 2;
        }
    }

    private static string ReadNewPassword()
    {
        Console.Write("Новый пароль (от 12 до 512 символов): ");
        var password = ReadSecret();
        AccountRules.ValidatePassword(password);
        Console.Write("Повтор пароля: ");
        if (password != ReadSecret()) throw new ArgumentException("Пароли не совпадают.");
        return password;
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var characters = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); return new string(characters.ToArray()); }
            if (key.Key == ConsoleKey.Backspace && characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            else if (!char.IsControl(key.KeyChar) && characters.Count < AccountRules.MaximumPasswordLength + 1)
                characters.Add(key.KeyChar);
        }
    }
}
