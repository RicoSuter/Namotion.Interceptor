using Namotion.Matter.Server;

namespace Namotion.Matter.Tests.Server;

/// <summary>A fake server with a connected controller and, once it listens, the nodes; disposes the controller and the server.</summary>
public sealed class FakeMatterServerSession(FakeMatterServer server, MatterServerController controller) : IAsyncDisposable
{
    public FakeMatterServer Server { get; } = server;

    public MatterServerController Controller { get; } = controller;

    public IReadOnlyList<MatterNode> Nodes { get; set; } = [];

    public async ValueTask DisposeAsync()
    {
        await Controller.DisposeAsync();
        await Server.DisposeAsync();
    }
}
