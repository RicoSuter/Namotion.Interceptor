using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HomeBlaze.Authentication.Blazor.Tests;

public class ForwardedHeadersTests
{
    [Fact]
    public async Task WhenNoProxyIsTrusted_ThenForwardedHeadersAreIgnored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();

        // Act
        var echo = await EchoAsync(host, "10.0.0.1");

        // Assert
        Assert.Equal("http|localhost|10.0.0.1|", echo);
    }

    [Fact]
    public async Task WhenNoProxyIsTrusted_ThenForwardedHeadersFromLoopbackAreIgnoredToo()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync();

        // Act
        var echo = await EchoAsync(host, "127.0.0.1");

        // Assert
        Assert.Equal("http|localhost|127.0.0.1|", echo);
    }

    [Fact]
    public async Task WhenTheRequestComesFromATrustedProxy_ThenItsForwardedHeadersAreApplied()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(
            settings: new Dictionary<string, string> { [$"{nameof(AuthenticationOptions.ForwardedHeadersKnownProxies)}:0"] = "10.0.0.1" });

        // Act
        var echo = await EchoAsync(host, "10.0.0.1");

        // Assert
        Assert.Equal("https|example.com|203.0.113.5|", echo);
    }

    [Fact]
    public async Task WhenTheRequestComesFromAnotherAddressThanTheTrustedProxy_ThenItsForwardedHeadersAreIgnored()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(
            settings: new Dictionary<string, string> { [$"{nameof(AuthenticationOptions.ForwardedHeadersKnownProxies)}:0"] = "10.0.0.1" });

        // Act
        var echo = await EchoAsync(host, "10.0.0.2");

        // Assert
        Assert.Equal("http|localhost|10.0.0.2|", echo);
    }

    [Fact]
    public async Task WhenTheRequestComesFromATrustedNetwork_ThenItsForwardedHeadersAreApplied()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(
            settings: new Dictionary<string, string> { [$"{nameof(AuthenticationOptions.ForwardedHeadersKnownNetworks)}:0"] = "10.0.0.0/8" });

        // Act
        var echo = await EchoAsync(host, "10.1.2.3");

        // Assert
        Assert.Equal("https|example.com|203.0.113.5|", echo);
    }

    [Theory]
    [InlineData(nameof(AuthenticationOptions.ForwardedHeadersKnownNetworks), "10.0.0.0/99")]
    [InlineData(nameof(AuthenticationOptions.ForwardedHeadersKnownProxies), "proxy.local")]
    public void WhenATrustedNetworkOrProxyIsInvalid_ThenTheErrorNamesTheSetting(string setting, string value)
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<AuthenticationOptions>(options =>
        {
            if (setting == nameof(AuthenticationOptions.ForwardedHeadersKnownNetworks))
            {
                options.ForwardedHeadersKnownNetworks = [value];
            }
            else
            {
                options.ForwardedHeadersKnownProxies = [value];
            }
        });
        services.AddHomeBlazeWebAuthentication();
        using var provider = services.BuildServiceProvider();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);

        // Assert
        Assert.Contains($"{AuthenticationOptions.SectionName}:{setting}", exception.Message);
        Assert.Contains(value, exception.Message);
    }

    [Fact]
    public async Task WhenAPathBaseIsConfigured_ThenRequestsBelowItAreRoutedAndRedirectsKeepIt()
    {
        // Arrange
        await using var host = await AuthenticationTestHost.StartAsync(
            settings: new Dictionary<string, string> { [nameof(AuthenticationOptions.PathBase)] = "/homeblaze" });
        var client = host.CreateClient();

        // Act
        var echo = await client.GetStringAsync("/homeblaze" + AuthenticationTestHost.EchoPath);
        var redirect = await client.GetAsync("/homeblaze/protected");

        // Assert
        Assert.EndsWith("|/homeblaze", echo);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/homeblaze/?ReturnUrl=%2Fhomeblaze%2Fprotected", redirect.Headers.Location!.PathAndQuery);
    }

    private static async Task<string> EchoAsync(AuthenticationTestHost host, string remoteAddress)
    {
        var context = await host.SendAsync(context =>
        {
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = AuthenticationTestHost.EchoPath;
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            context.Request.Headers["X-Forwarded-For"] = "203.0.113.5";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
            context.Request.Headers["X-Forwarded-Host"] = "example.com";
        });

        using var reader = new StreamReader(context.Response.Body);
        return await reader.ReadToEndAsync();
    }
}
