using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Mcp;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Mcp;
using Namotion.Interceptor.Mcp.Tools;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace HomeBlaze.AI.Tests.Agents;

[InterceptorSubject]
public partial class AgentTestRoot
{
    public AgentTestRoot()
    {
        Children = new Dictionary<string, IInterceptorSubject>();
    }

    [State]
    public partial decimal Temperature { get; set; }

    [State]
    public partial decimal Humidity { get; set; }

    [InlinePaths]
    public partial Dictionary<string, IInterceptorSubject> Children { get; set; }
}

// A plain subclass: it adds no intercepted properties, so it needs no generated code and avoids
// cross-assembly subject inheritance in the generator.
public class FakeAgent : LlmAgentBase
{
    public FakeAgent(AgentServices services, ILogger? logger = null, IReadOnlyList<AgentBackendTool>? supportedBackendTools = null)
        : base(services, logger ?? NullLogger.Instance, supportedBackendTools)
    {
    }

    public Func<AgentTurnRequest, IProgress<AgentTurnUpdate>, CancellationToken, Task<AgentTurnResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new AgentTurnResult("[quiet] nothing new", null, null));

    public List<AgentTurnRequest> Requests { get; } = [];

    public int ResetCount { get; private set; }

    protected override Task<AgentTurnResult> ExecuteTurnAsync(AgentTurnRequest request, IProgress<AgentTurnUpdate> progress, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add(request);
        }

        return Handler(request, progress, cancellationToken);
    }

    protected override Task ResetSessionAsync(CancellationToken cancellationToken)
    {
        ResetCount++;
        return Task.CompletedTask;
    }
}

internal sealed class TestAgentToolSource : IAgentToolSource
{
    private readonly Func<IInterceptorSubject> _root;

    public TestAgentToolSource(Func<IInterceptorSubject> root) => _root = root;

    public IReadOnlyList<McpToolInfo> CreateTools(bool isReadOnly)
    {
        var configuration = new McpServerConfiguration
        {
            PathProvider = new StateAttributePathProvider(),
            PathPrefix = "/",
            IsReadOnly = isReadOnly
        };
        return new McpToolFactory(_root, configuration).CreateTools();
    }
}

internal sealed class FixedEndpointAddress : IAgentEndpointAddress
{
    private readonly Uri? _endpoint;

    public FixedEndpointAddress(Uri? endpoint) => _endpoint = endpoint;

    public Uri? GetAgentEndpoint() => _endpoint;
}

internal sealed class TestDataDirectory : IDataDirectoryProvider
{
    public TestDataDirectory(string dataDirectory) => DataDirectory = dataDirectory;

    public string DataDirectory { get; }
}

/// <summary>
/// A graph with a root, an agent attached under /Agent (or another name), path resolution and a data directory.
/// </summary>
internal sealed class AgentTestContext<TAgent> : IAsyncDisposable
    where TAgent : LlmAgentBase
{
    private AgentTestContext(string dataDirectory, AgentTestRoot root, TAgent agent, AgentServices services)
    {
        DataDirectory = dataDirectory;
        Root = root;
        Agent = agent;
        Services = services;
    }

    public string DataDirectory { get; }

    public AgentTestRoot Root { get; }

    public TAgent Agent { get; }

    public AgentServices Services { get; }

    public static async Task<AgentTestContext<TAgent>> StartAsync(
        Func<AgentServices, TAgent> createAgent,
        Action<TAgent>? configure = null,
        Uri? endpoint = null,
        AgentTurnTokenRegistry? tokenRegistry = null,
        TimeProvider? timeProvider = null,
        string agentName = "Agent")
    {
        var dataDirectory = Directory.CreateTempSubdirectory("agent-test").FullName;
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);

        var root = new AgentTestRoot(context);
        context.AddService(new SubjectPathResolver(() => root));
        context.AddService<IDataDirectoryProvider>(new TestDataDirectory(dataDirectory));

        var services = new AgentServices(
            new TestAgentToolSource(() => root),
            tokenRegistry ?? new AgentTurnTokenRegistry(),
            new FixedEndpointAddress(endpoint),
            timeProvider ?? TimeProvider.System);
        var agent = createAgent(services);
        agent.MinRunInterval = TimeSpan.Zero;
        configure?.Invoke(agent);
        root.Children = new Dictionary<string, IInterceptorSubject> { [agentName] = agent };

        await ((IHostedService)agent).StartAsync(CancellationToken.None);
        await Namotion.Interceptor.Testing.AsyncTestHelpers.WaitUntilAsync(
            () => agent.Status is not (ServiceStatus.Stopped or ServiceStatus.Starting), message: "The agent should finish starting.");
        return new AgentTestContext<TAgent>(dataDirectory, root, agent, services);
    }

    public async ValueTask DisposeAsync()
    {
        await ((IHostedService)Agent).StopAsync(CancellationToken.None);
        Directory.Delete(DataDirectory, recursive: true);
    }
}
