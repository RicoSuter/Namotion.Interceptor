using HomeBlaze.AI.Mcp;
using HomeBlaze.Authentication.Data;
using HomeBlaze.Authorization;
using HomeBlaze.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Testing;
using Xunit;
using AuthorizationSubject = HomeBlaze.Authorization.Authorization;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class AuthenticationStartupTests
{
    [Fact]
    public async Task WhenTheRootNeverLoads_ThenTheStartReturnsAndTheDatabaseStaysUnavailable()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("homeblaze-authentication-");
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection([new(HomeBlazePaths.RootConfigurationFileKey, Path.Combine(directory.FullName, "Root.json"))])
                .Build();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddHomeBlazeServices();
            services.AddHomeBlazeAuthorization(new StateAttributePathProvider());
            services.AddDataProtection();
            services.AddHomeBlazeAuthentication(configuration);
            await using var provider = services.BuildServiceProvider();
            // The root manager is not started, so the root never loads.
            var startup = provider.GetServices<IHostedService>().OfType<AuthenticationStartup>().Single();

            // Act
            await startup.StartAsync(CancellationToken.None);

            // Assert
            Assert.False(provider.GetRequiredService<AuthenticationDatabase>().IsAvailable);
            var executeTask = startup.ExecuteTask;
            Assert.NotNull(executeTask);
            Assert.False(executeTask.IsCompleted);
            await startup.StopAsync(CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task WhenNoAuthorizationExists_ThenOneIsAddedToTheRootStorageAndShowsTheUsers()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();

        // Act
        var authorizations = host.AuthorizationSubjects;
        var isFileAdded = File.Exists(Path.Combine(host.FilesDirectory, "Authorization.json"));

        // Assert
        Assert.True(host.Database.IsReady);
        Assert.True(isFileAdded);
        var authorization = Assert.Single(authorizations);
        Assert.Contains(AdminSeeding.AdminUserName, authorization.Users.Keys);
        Assert.Contains(NameRules.AnonymousUserName, authorization.Users.Keys);
    }

    [Fact]
    public async Task WhenAnAuthorizationIsStoredUnderAnotherName_ThenNoOtherIsAdded()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(authorizationFileName: "Access.json");

        // Act
        var authorizations = host.AuthorizationSubjects;
        var isFileAdded = File.Exists(Path.Combine(host.FilesDirectory, "Authorization.json"));

        // Assert
        Assert.False(isFileAdded);
        var authorization = Assert.Single(authorizations);
        Assert.Contains(AdminSeeding.AdminUserName, authorization.Users.Keys);
    }

    [Fact]
    public async Task WhenTheAuthorizationIsReplaced_ThenTheNewOneShowsTheUsers()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        var original = Assert.Single(host.AuthorizationSubjects);
        var replacement = new AuthorizationSubject();

        // Act
        await host.Root.DeleteSubjectAsync(original, CancellationToken.None);
        await host.Root.AddSubjectAsync("Access.json", replacement, CancellationToken.None);

        // Assert
        await AsyncTestHelpers.WaitUntilAsync(() => replacement.Users.ContainsKey(AdminSeeding.AdminUserName));
    }

    [Fact]
    public async Task WhenTheServicesAreResolved_ThenTheContextFactoryIsASingletonAndTheContextIsScoped()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();
        await using var firstScope = host.Services.CreateAsyncScope();
        await using var secondScope = host.Services.CreateAsyncScope();

        // Act
        var factory = host.Services.GetRequiredService<IDbContextFactory<AuthenticationDbContext>>();
        var first = firstScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var firstAgain = firstScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        var second = secondScope.ServiceProvider.GetRequiredService<AuthenticationDbContext>();
        await using var created = await factory.CreateDbContextAsync();

        // Assert
        Assert.Same(first, firstAgain);
        Assert.NotSame(first, second);
        Assert.NotSame(first, created);
        Assert.Equal(first.Database.GetConnectionString(), created.Database.GetConnectionString());
        Assert.True(await created.Users.AnyAsync(user => user.UserName == AdminSeeding.AdminUserName));
    }
}
