using System.Net;
using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Clients;
using HomeBlaze.Game.Tests.Testing;
using HomeBlaze.Installation;
using HomeBlaze.Services;
using HomeBlaze.Services.Clients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Testing;

namespace HomeBlaze.Game.Tests.Clients;

public sealed class InstallationClientDirectoryTests : IAsyncLifetime
{
    private readonly ClientConnectionService _connections = new(TimeProvider.System, NullLogger<ClientConnectionService>.Instance);
    private readonly TestRoot _root = new(SubjectContextFactory.Create(new ServiceCollection()));
    private readonly HomeBlazeInstallation _installation;
    private readonly InstallationClientDirectory _directory;

    public InstallationClientDirectoryTests()
    {
        _installation = new HomeBlazeInstallation(
            new HostingEnvironment { EnvironmentName = "Production" },
            new NotFoundHttpClientFactory(),
            new DeveloperModeService(),
            _connections,
            NullLogger<HomeBlazeInstallation>.Instance);
        _root.Installation = _installation;
        _directory = new InstallationClientDirectory(() => ((IInterceptorSubject)_root).Context);
    }

    public Task InitializeAsync() => _installation.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await _installation.StopAsync(CancellationToken.None);
        _installation.Dispose();
    }

    [Fact]
    public async Task WhenAnAnonymousClientIsRenamed_ThenItIsNamedAndTheInstallationIsSaved()
    {
        // Arrange
        var connection = _connections.Connect(null, new NoClientCallbacks());
        var clientId = connection.ConnectionId;
        await AsyncTestHelpers.WaitUntilAsync(() => _directory.GetClients()?.Any(client => client.Id == clientId) == true,
            message: "The client did not appear");
        var writeCount = _root.WriteCount;

        // Act
        await _directory.RenameAsync(clientId, "Kitchen", CancellationToken.None);

        // Assert
        var client = Assert.Single(_directory.GetClients() ?? []);
        Assert.Equal(new GameClient(clientId, "Kitchen", IsNamed: true, IsConnected: true), client);
        Assert.Equal("Kitchen", _installation.Clients[clientId].Name);
        Assert.True(_root.WriteCount > writeCount);
    }

    [Fact]
    public async Task WhenTheClientIsUnknown_ThenRenamingIsRefused()
    {
        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.RenameAsync("unknown", "Kitchen", CancellationToken.None));
    }

    private sealed class NotFoundHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFoundHandler());

        private sealed class NotFoundHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NoClientCallbacks : IClientConnectionCallbacks
    {
        public Task ShowNotificationAsync(Notification notification, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StoreClientTokenAsync(string token) => Task.CompletedTask;
    }
}
