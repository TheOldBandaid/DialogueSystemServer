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
            default:
                Console.Error.WriteLine("Неизвестная команда обслуживания.");
                return 2;
        }
    }
}
