using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Hosting;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Benchmark;

/// <summary>
/// The shape of the graph attached start path: the cost of a set of hosted subjects entering the graph
/// together should be constant in how many of them there are, not linear.
/// </summary>
/// <remarks>
/// Every start carries a fixed delay before it touches its instance, and each start runs on its own
/// target's chain, so the delays overlap and one set of subjects pays one delay rather than one each.
/// An earlier implementation posted every start to one shared consumer loop, which paid them in series;
/// this benchmark is what tells a change that reintroduces that serialization from one that does not.
/// Read it by comparing the two <see cref="SubjectCount"/> rows against each other rather than by their
/// absolute values, which are the delay constant plus whatever the machine adds.
/// <para>
/// Monitoring rather than the default strategy, because one operation is tens of milliseconds of
/// waiting: the pilot stage and the many invocations per iteration the default uses would add minutes
/// to every run for no extra resolution.
/// </para>
/// </remarks>
[SimpleJob(RunStrategy.Monitoring, launchCount: 1, warmupCount: 1, iterationCount: 5)]
public class HostedServiceStartupBenchmark
{
    private Microsoft.Extensions.Hosting.IHost _host = null!;
    private HostedSubjectGraph _graph = null!;

    [Params(1, 32)]
    public int SubjectCount;

    [GlobalSetup]
    public void Setup()
    {
        var builder = Host.CreateApplicationBuilder();

        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        _host = builder.Build();
        _host.StartAsync().GetAwaiter().GetResult();

        _graph = new HostedSubjectGraph(context);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _host.StopAsync().GetAwaiter().GetResult();
        _host.Dispose();
    }

    /// <summary>
    /// Lets a whole set of hosted subjects enter the graph in one write and waits for every one of
    /// them to have started. The write also detaches the previous set, whose stops run on their own
    /// chains and are not waited for.
    /// </summary>
    [Benchmark]
    public void AttachHostedSubjectsAndWaitForTheirStarts()
    {
        var started = new CountdownEvent(SubjectCount);
        var children = new BenchmarkHostedSubject[SubjectCount];
        for (var index = 0; index < SubjectCount; index++)
        {
            children[index] = new BenchmarkHostedSubject { Started = started };
        }

        _graph.Children = children;
        started.Wait();
        started.Dispose();
    }
}

[InterceptorSubject]
public partial class HostedSubjectGraph
{
    public partial BenchmarkHostedSubject[]? Children { get; set; }
}

[InterceptorSubject]
public partial class BenchmarkHostedSubject : IHostedService
{
    public partial string? Name { get; set; }

    /// <summary>Signalled once this subject's start has run, so the benchmark waits for a fact.</summary>
    public CountdownEvent? Started { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started?.Signal();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
