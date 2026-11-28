using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

/// <summary>A fake server with a connected controller and, once it listens, the nodes; disposes the controller and the server.</summary>
public sealed record FakeMatterServerSession(FakeMatterServer Server, MatterServerController Controller) : IAsyncDisposable
{
    public IReadOnlyList<MatterNode> Nodes { get; init; } = [];

    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        await Server.DisposeAsync();
    }
}
