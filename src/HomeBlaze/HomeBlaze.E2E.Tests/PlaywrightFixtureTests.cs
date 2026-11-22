using HomeBlaze.Abstractions.Discovery;
using HomeBlaze.E2E.Tests.Infrastructure;
using HomeBlaze.Services;
using HomeBlaze.Storage;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Devices.Nuki;
using Xunit;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// What the tests rely on from the fixture itself.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
[Trait("Category", "Integration")]
public class PlaywrightFixtureTests
{
    private readonly PlaywrightFixture _fixture;

    public PlaywrightFixtureTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void WhenTheFixtureStartedTheServer_ThenTheBrowserAndTheServicesUseItsOnlyHost()
    {
        // Arrange
        var serverAddress = new Uri(_fixture.ServerAddress);

        // Act
        var addresses = _fixture.ServerServices.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;

        // Assert
        Assert.Equal(1, _fixture.StartedHostCount);
        Assert.Contains(addresses, address => new Uri(address) == serverAddress);
    }

    [Fact]
    public async Task WhenTheDiscoverersOfTheApplicationScan_ThenTheyFindNothingWithoutTheNetwork()
    {
        // Arrange
        var services = _fixture.ServerServices;
        using var httpClient = services.GetRequiredService<IHttpClientFactory>().CreateClient();

        // Act
        var ssdpResponses = await services.GetRequiredService<ISsdpSearch>().SearchAsync("ssdp:all", CancellationToken.None);
        var multicastDnsServices = await services.GetRequiredService<IMulticastDnsBrowser>().BrowseAsync("_hue._tcp", CancellationToken.None);
        var nukiAnswer = await httpClient.GetStringAsync(NukiBridgeDiscovery.DiscoveryAddress);

        // Assert
        Assert.Empty(ssdpResponses);
        Assert.Empty(multicastDnsServices);
        // The stub of the host answers with exactly this; the real service adds an error code.
        Assert.Equal("{\"bridges\":[]}", nukiAnswer);
    }

    [Fact]
    public async Task WhenAnEarlierTestLeftAChangeOnDisk_ThenAPassOfTheStorageKeepsThePlainSubjectAddedAfterwards()
    {
        // Arrange: a file that the storage knows is deleted behind it, as a test does that cleans up on disk.
        var root = (FluentStorageContainer)_fixture.ServerServices.GetRequiredService<RootManager>().Root!;
        const string fileName = "LeftBehind.md";
        var filePath = Path.Combine(AppContext.BaseDirectory, "TestData", fileName);
        await File.WriteAllTextAsync(filePath, "# Left behind");
        await root.ReconcileAsync();
        var isFileKnown = root.Children.ContainsKey(fileName);
        File.Delete(filePath);

        var (name, subject) = await _fixture.AddPlainSubjectToRootAsync();
        try
        {
            // Act: the pass that an earlier test asked for runs while this test uses its subject.
            await root.ReconcileAsync();

            // Assert
            Assert.True(isFileKnown);
            Assert.False(root.Children.ContainsKey(fileName));
            Assert.Same(subject, root.Children.GetValueOrDefault(name));
        }
        finally
        {
            _fixture.RemoveSubjectFromRoot(name, subject);
        }
    }
}
