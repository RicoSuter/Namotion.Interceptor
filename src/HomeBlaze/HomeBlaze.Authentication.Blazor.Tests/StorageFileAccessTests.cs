using System.Net;
using HomeBlaze.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

/// <summary>The files of the storages are served only to a person or visitor to whom the file is shown.</summary>
public class StorageFileAccessTests
{
    private const string PlanContent = "plan";

    private static readonly IReadOnlyDictionary<string, string> Files = new Dictionary<string, string>
    {
        ["Plans/plan.txt"] = PlanContent,
        ["notes.txt"] = "notes"
    };

    [Fact]
    public async Task WhenThePersonMayReadTheFile_ThenItsContentIsServed()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(Files);
        using var browser = await SignInWithRolesAsync(host, [BuiltInRoles.ReaderName]);

        // Act
        using var response = await browser.GetAsync("/files/Plans/plan.txt");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(PlanContent, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task WhenTheFileIsInABranchTheRolesHide_ThenItIsAnsweredAsMissing()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "no-plans",
            Includes = [BuiltInRoles.ReaderName],
            // Only the members of the folder, so that the members of the file stay readable.
            Deny = [new PermissionRule { Paths = ["/Plans/*"], Actions = [PermissionAction.Read] }]
        };
        await using var host = await AuthenticationTestHost.StartAsync(Files);
        using var browser = await SignInWithRolesAsync(host, [role.Name], role);

        // Act
        using var hidden = await browser.GetAsync("/files/Plans/plan.txt");
        using var missing = await browser.GetAsync("/files/Plans/missing.txt");
        using var shown = await browser.GetAsync("/files/notes.txt");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Empty(await hidden.Content.ReadAsByteArrayAsync());
        Assert.Equal(missing.StatusCode, hidden.StatusCode);
        Assert.Equal(missing.Content.Headers.ContentType, hidden.Content.Headers.ContentType);
        Assert.Equal(HttpStatusCode.OK, shown.StatusCode);
    }

    [Fact]
    public async Task WhenTheRolesDenyReadingTheFile_ThenItIsAnsweredAsMissing()
    {
        // Arrange
        var role = new AuthorizationRole
        {
            Name = "no-plan",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Plans/plan.txt/*"], Actions = [PermissionAction.Read] }]
        };
        await using var host = await AuthenticationTestHost.StartAsync(Files);
        using var browser = await SignInWithRolesAsync(host, [role.Name], role);

        // Act
        using var response = await browser.GetAsync("/files/Plans/plan.txt");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WhenAVisitorIsNotSignedInAndAnonymousHoldsNoRoles_ThenTheFileIsNotServed()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(Files);
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync("/files/Plans/plan.txt");

        // Assert
        Assert.Equal("/", TestBrowser.GetRedirectPath(response).Split('?')[0]);
    }

    [Theory]
    [InlineData(BuiltInRoles.ReaderName, HttpStatusCode.OK)]
    [InlineData("no-plans", HttpStatusCode.NotFound)]
    public async Task WhenAVisitorIsNotSignedIn_ThenTheRolesOfAnonymousDecide(string anonymousRole, HttpStatusCode expected)
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(Files);
        var saved = await host.Services.GetRequiredService<RoleStore>().SaveRoleAsync(null, new AuthorizationRole
        {
            Name = "no-plans",
            Includes = [BuiltInRoles.ReaderName],
            Deny = [new PermissionRule { Paths = ["/Plans/**"], Actions = [PermissionAction.Read] }]
        }, null, CancellationToken.None);
        Assert.True(saved.Succeeded, saved.Error);
        var result = await host.AdministrationAsAdmin.SetRolesAsync(NameRules.AnonymousUserName, [anonymousRole], CancellationToken.None);
        Assert.True(result.Succeeded, result.Error);
        using var browser = host.CreateBrowser();

        // Act
        using var response = await browser.GetAsync("/files/Plans/plan.txt");

        // Assert
        Assert.Equal(expected, response.StatusCode);
    }

    private static async Task<TestBrowser> SignInWithRolesAsync(AuthenticationTestHost host, IReadOnlyCollection<string> roles, AuthorizationRole? customRole = null)
    {
        if (customRole is not null)
        {
            var saved = await host.Services.GetRequiredService<RoleStore>().SaveRoleAsync(null, customRole, null, CancellationToken.None);
            Assert.True(saved.Succeeded, saved.Error);
        }

        var added = await host.AdministrationAsAdmin.AddUserAsync("kid", null, roles, CancellationToken.None);
        Assert.True(added.Succeeded, added.Error);
        var browser = host.CreateBrowser();
        await host.SignInAsync(browser, "kid");
        return browser;
    }
}
