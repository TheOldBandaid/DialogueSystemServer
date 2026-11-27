using System.Net;
using System.Reflection;
using System.Text.Json;
using DialogueSystem.Application;
using DialogueSystem.Domain;
using DialogueSystem.Infrastructure.Data;
using DialogueSystem.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DialogueSystem.Tests;

public sealed class ApiContractTests
{
    [Fact]
    public async Task AllApiMutationsRequireCsrfAndDataRoutesRequireAuthentication()
    {
        using var data = new DialogueTestData();
        await using var app = data.Start();
        var actions = app.Services.GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items
            .OfType<ControllerActionDescriptor>().Where(action => action.AttributeRouteInfo?.Template?.StartsWith("api/", StringComparison.Ordinal) == true).ToArray();
        Assert.NotEmpty(actions);
        foreach (var action in actions)
        {
            var attributes = action.ControllerTypeInfo.GetCustomAttributes().Concat(action.MethodInfo.GetCustomAttributes()).ToArray();
            if (action.MethodInfo.Name is not ("Csrf" or "Login") || action.ControllerTypeInfo.AsType() != typeof(AccountsController))
                Assert.Contains(attributes, attribute => attribute is IAuthorizeData);
            var methods = action.MethodInfo.GetCustomAttributes<HttpMethodAttribute>().SelectMany(attribute => attribute.HttpMethods).ToArray();
            if (methods.Any(method => method is not ("GET" or "HEAD" or "OPTIONS")))
                Assert.Contains(attributes, attribute => attribute is AutoValidateAntiforgeryTokenAttribute or ValidateAntiForgeryTokenAttribute);
            Assert.DoesNotContain(attributes, attribute => attribute is IgnoreAntiforgeryTokenAttribute or AllowAnonymousAttribute);
        }
    }

    [Fact]
    public async Task AccountPagesAreBoundedAndRolesRejectOutdatedForms()
    {
        using var data = new DialogueTestData();
        var password = Guid.NewGuid().ToString("N");
        await using var app = data.Start(settings: [new("DialogueSystem:AdminPassword", password), new("DialogueSystem:EditorPassword", password)]);
        using var admin = new DialogueApiClient(app);
        await admin.Login("admin", password);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DialogueDbContext>();
            var hash = await db.Accounts.Select(account => account.PasswordHash).FirstAsync();
            for (var index = 0; index < 105; index++) db.Add(new Account { Login = $"user{index:000}", Role = AccountRoles.Editor, PasswordHash = hash });
            await db.SaveChangesAsync();
        }
        Assert.Equal(107, (await admin.GetAll<AccountSummary>("/api/accounts")).Length);
        var page = await admin.Get<CatalogPage<AccountSummary>>("/api/accounts?q=USER");
        Assert.Equal(20, page.Items.Count);
        Assert.NotNull(page.NextBeforeId);
        var next = await admin.Get<CatalogPage<AccountSummary>>($"/api/accounts?q=USER&before={page.NextBeforeId}");
        Assert.Empty(page.Items.Select(item => item.Id).Intersect(next.Items.Select(item => item.Id)));
        var target = page.Items[0];
        var path = $"/api/accounts/{target.Id}/role";
        await admin.Expect(HttpStatusCode.NoContent, HttpMethod.Put, path, new { role = AccountRoles.Administrator, target.Version });
        await admin.Expect(HttpStatusCode.Conflict, HttpMethod.Put, path, new { role = AccountRoles.Editor, target.Version });
        using (var missing = await admin.Send(HttpMethod.Put, path, new { role = AccountRoles.Editor }, includeVersion: false)) await Problem(missing, 428);
        Assert.Equal(AccountRoles.Administrator, (await admin.Get<AccountSummary>($"/api/accounts/{target.Id}")).Role);
    }

    private static async Task Problem(HttpResponseMessage response, int status)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(status, json.RootElement.GetProperty("status").GetInt32());
    }
}
