using DialogueSystem.Web.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DialogueSystem.Tests;

internal sealed class DialogueTestData : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("dialogue-tests-");

    public string DatabasePath => Path.Combine(directory.FullName, "dialogues.db");

    public WebApplicationFactory<DialogueDbContext> Start(
        string? environment = null, params KeyValuePair<string, string?>[] settings) =>
        new DialogueApplication(directory.FullName, environment ?? Environments.Development, settings);

    public void Dispose()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            ForeignKeys = true
        };
        using var connection = new SqliteConnection(connectionString.ToString());
        SqliteConnection.ClearPool(connection);
        directory.Delete(recursive: true);
    }

    private sealed class DialogueApplication(
        string dataDirectory, string environment, KeyValuePair<string, string?>[] settings)
        : WebApplicationFactory<DialogueDbContext>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            builder.ConfigureHostConfiguration(configuration => configuration
                .AddInMemoryCollection(settings)
                .AddInMemoryCollection([new("DialogueSystem:DataDirectory", dataDirectory)]));
            return base.CreateHost(builder);
        }
    }
}
