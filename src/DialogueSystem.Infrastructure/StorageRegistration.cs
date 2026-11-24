using DialogueSystem.Application;
using DialogueSystem.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Infrastructure;

public static class StorageRegistration
{
    public static IServiceCollection AddDialogueStorage(this IServiceCollection services, string dataDirectory)
    {
        var connection = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "dialogues.db"),
            ForeignKeys = true
        };
        return services
            .AddDbContext<DialogueDbContext>(options => options.UseSqlite(connection.ToString()))
            .AddScoped<IAccountStore, SqliteAccounts>()
            .AddScoped<IPasswordService, PasswordService>()
            .AddScoped<IDialogueCatalog, SqliteDialogueCatalog>()
            .AddScoped<IDialogueRevisions, SqliteRevisions>()
            .AddScoped<ICharacterCatalog, SqliteCharacterCatalog>()
            .AddScoped<ILineStore, SqliteLines>()
            .AddScoped<IPreviewStore, SqlitePreviews>();
    }

    public static async Task InitializeDialogueDatabaseAsync(
        this IServiceProvider services, bool includeExamples, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<DialogueDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
        if (includeExamples)
            await DemoDialogue.SeedAsync(db, cancellationToken);
    }
}
