using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// A subject that hosts nothing: no <c>IHostedService</c>, no attachment. This is what almost every
/// subject in a real graph looks like, and it is the case the attach and detach lifecycle callbacks
/// pay for on every graph mutation.
/// </summary>
[InterceptorSubject]
public partial class HostingLeaf
{
    public HostingLeaf()
    {
        Value = "";
    }

    public partial string Value { get; set; }
}

/// <summary>
/// A leaf that does host something. One of these in the graph is what an application with any hosted
/// subject at all looks like, and it is the case the fast path must not break.
/// </summary>
[InterceptorSubject]
public partial class HostingWorkerLeaf : HostingLeaf, IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class HostingGraphRoot
{
    public HostingGraphRoot()
    {
        Items = [];
    }

    public partial HostingLeaf[] Items { get; set; }
}

/// <summary>
/// Separates the cost of having one more handler in the lifecycle chain from the cost of that
/// handler's body.
/// </summary>
internal sealed class EmptyLifecycleHandler : ILifecycleHandler
{
    public void HandleLifecycleChange(SubjectLifecycleChange change)
    {
    }
}

public enum HostingArm
{
    /// <summary>Registry only. Hosting is not enabled at all.</summary>
    None,

    /// <summary>Registry plus a lifecycle handler whose body does nothing.</summary>
    EmptyHandler,

    /// <summary>Registry plus <c>WithHostedServices</c>, on a graph where nothing is hosted.</summary>
    Hosting,

    /// <summary>Registry plus <c>WithHostedServices</c>, on a graph where one subject is hosted.</summary>
    HostingOneHosted
}

/// <summary>
/// What enabling hosting costs on a graph in which nothing is hosted.
/// </summary>
/// <remarks>
/// The graph is attached and detached by one array assignment each way, so a single measured
/// operation is <see cref="SubjectCount"/> attach or detach lifecycle callbacks. The host is never
/// started: the attach path reads the gate state and takes the same branches whether the gate is
/// NotStarted or Running, and starting a host would add a background loop whose allocations the
/// process wide memory diagnoser would absorb.
/// </remarks>
[MemoryDiagnoser]
[InvocationCount(1)]
[WarmupCount(8)]
[IterationCount(40)]
public class HostingLifecycleBenchmark
{
    private IInterceptorSubjectContext _warmContext = null!;
    private HostingGraphRoot _warmRoot = null!;

    private HostingGraphRoot _root = null!;
    private HostingLeaf[] _items = null!;

    [Params(HostingArm.None, HostingArm.EmptyHandler, HostingArm.Hosting, HostingArm.HostingOneHosted)]
    public HostingArm Arm;

    [Params(20_000)]
    public int SubjectCount;

    private IInterceptorSubjectContext CreateContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry();

        switch (Arm)
        {
            case HostingArm.EmptyHandler:
                context.WithService(() => new EmptyLifecycleHandler());
                break;

            case HostingArm.Hosting:
            case HostingArm.HostingOneHosted:
                context.WithHostedServices(new ServiceCollection());
                break;
        }

        return context;
    }

    private HostingLeaf[] CreateItems()
    {
        var items = new HostingLeaf[SubjectCount];
        for (var index = 0; index < items.Length; index++)
        {
            items[index] = new HostingLeaf();
        }

        if (Arm == HostingArm.HostingOneHosted)
        {
            // One worker among twenty thousand. The host is never started, so its start is appended
            // and never runs, which keeps the measurement on the callback path rather than on a
            // background loop.
            items[0] = new HostingWorkerLeaf();
        }

        return items;
    }

    [GlobalSetup(Targets = [nameof(AttachWarm)])]
    public void SetupWarm()
    {
        _warmContext = CreateContext();
        _warmRoot = new HostingGraphRoot(_warmContext);

        // One full cycle so every dictionary behind the handlers has grown its table before the
        // first measured attach. A ConcurrentDictionary never shrinks on removal, so from here on
        // an attach finds the table already sized.
        _warmRoot.Items = CreateItems();
        _warmRoot.Items = [];
    }

    /// <summary>
    /// Steady state: the context has already carried a graph of this size, so no table growth.
    /// </summary>
    [IterationSetup(Target = nameof(AttachWarm))]
    public void PrepareAttachWarm()
    {
        _warmRoot.Items = [];
        _items = CreateItems();
    }

    [Benchmark]
    public void AttachWarm()
    {
        _warmRoot.Items = _items;
    }

    /// <summary>
    /// First ever attach onto a fresh context, which is what an application startup does.
    /// </summary>
    [IterationSetup(Target = nameof(AttachCold))]
    public void PrepareAttachCold()
    {
        _root = new HostingGraphRoot(CreateContext());
        _items = CreateItems();
    }

    [Benchmark]
    public void AttachCold()
    {
        _root.Items = _items;
    }

    [IterationSetup(Target = nameof(Detach))]
    public void PrepareDetach()
    {
        _root = new HostingGraphRoot(CreateContext());
        _root.Items = CreateItems();
    }

    [Benchmark]
    public void Detach()
    {
        _root.Items = [];
    }
}
