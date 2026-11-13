using HomeBlaze.Abstractions.Messaging;
using HomeBlaze.Game.Clients;
using Namotion.Interceptor.Tracking.Change;

namespace HomeBlaze.Game.Benchmark;

/// <summary>
/// Connected anonymous clients; used only on the thread that runs the benchmark.
/// </summary>
internal sealed class BenchmarkClientDirectory : IGameClientDirectory
{
    private readonly List<GameClient> _clients = [];

    public void Add(string clientId) => _clients.Add(new GameClient(clientId, clientId, IsNamed: false, IsConnected: true));

    public IReadOnlyList<GameClient> GetClients() => _clients;

    public Task SendNotificationAsync(string clientId, Notification notification, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task RenameAsync(string clientId, string name, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsClientChange(SubjectPropertyChange change) => false;
}
