using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Testing;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class ReadinessTests
{
    [Fact]
    public async Task WhenTheStartupHasNotCompleted_ThenPagesAndTheMcpEndpointAreUnavailable()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(rootType: typeof(NeverLoadingStorageRoot), waitForStartup: false);
        var client = host.CreateClient();

        // Act
        var pageResponse = await client.GetAsync("/protected");
        var mcpResponse = await client.PostAsync(AuthenticationTestHost.McpPath, content: null);

        // Assert
        Assert.True(host.Database.IsAvailable);
        Assert.False(host.Database.IsReady);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pageResponse.StatusCode);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains("authentication unavailable", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("HomeBlaze is starting.", page);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, mcpResponse.StatusCode);
    }

    [Fact]
    public async Task WhenTheRootStorageFailedToLoad_ThenAWarningSaysWhyAuthenticationStaysUnavailable()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(rootType: typeof(FailedStorageRoot), logger: logger, waitForStartup: false);

        // Act
        await AsyncTestHelpers.WaitUntilAsync(() => logger.Warnings.Any(message => message.Contains("root storage failed to load", StringComparison.Ordinal)));

        // Assert
        Assert.False(host.Database.IsReady);
    }

    [Fact]
    public async Task WhenShowingTheRolesAndUsersFails_ThenTheHostKeepsRunningAndAuthenticationIsUnavailable()
    {
        // Arrange
        var logger = new RecordingLogger();
        await using var host = await AuthenticationTestHost.StartAsync(rootType: typeof(BrokenStorageRoot), logger: logger);
        var client = host.CreateClient();

        // Act
        var aliveResponse = await client.GetAsync("/alive");
        var pageResponse = await client.GetAsync("/protected");

        // Assert
        var startup = host.Services.GetServices<IHostedService>().OfType<AuthenticationStartup>().Single();
        Assert.True(startup.ExecuteTask?.IsCompletedSuccessfully);
        Assert.False(host.Database.IsAvailable);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception?.Message == "The storage is broken.");
        Assert.Equal(HttpStatusCode.OK, aliveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, pageResponse.StatusCode);
        var page = await pageResponse.Content.ReadAsStringAsync();
        Assert.Contains(AuthenticationStartup.BindingFailedReason, page);
        Assert.DoesNotContain("--reset-admin", page);
        Assert.DoesNotContain("authentication database", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhenTheDatabaseIsNotReady_ThenTheMcpKeyCheckRefusesWithoutCallingTheEndpoint()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(rootType: typeof(NeverLoadingStorageRoot), waitForStartup: false);
        var isEndpointCalled = false;
        var middleware = new McpClientKeyMiddleware(_ => { isEndpointCalled = true; return Task.CompletedTask; }, host.UserStore, host.Database);
        var context = new DefaultHttpContext();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        Assert.False(host.Database.IsReady);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.False(isEndpointCalled);
    }
}
