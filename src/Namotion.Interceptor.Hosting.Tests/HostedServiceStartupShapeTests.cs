using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Hosting.Tests;

/// <summary>
/// The graph attached start path must stay constant in the number of subjects entering the graph
/// together, not linear. Every start carries a fixed delay before it touches its instance, and each
/// runs on its own target's chain, so the delays overlap and a set of subjects pays one delay rather
/// than one each. An earlier implementation posted every start to one shared consumer loop and paid
/// them in series.
/// </summary>
/// <remarks>
/// This pins that the per subject cost does not accumulate, and nothing else. It does not pin that
/// each target's own transitions are serialized, which
/// <see cref="HostedServiceTargetTests.WhenTransitionsAreAppendedConcurrently_ThenTheyNeverOverlap"/>
/// covers: an unsynchronized free for all would overlap just as well and pass here.
/// <para>
/// It also depends on <c>HostedServiceHandler.StartDelayMilliseconds</c> being large enough to
/// dominate both measurements, which is what makes a healthy ratio sit near one and leaves room for a
/// tolerance of four. Whoever removes that delay has to revisit this test rather than assume it still
/// works: the denominator becomes sub millisecond, so scheduling jitter alone can exceed the tolerance,
/// and the regression itself shrinks to thirty two times a fraction of a millisecond, which timing
/// cannot see reliably. At that point this test needs a different mechanism or an honest deletion.
/// </para>
/// </remarks>
public class HostedServiceStartupShapeTests
{
    private const int ManySubjects = 32;

    /// <summary>
    /// Deliberately a ratio and deliberately generous. Serialized starts separate the two measurements
    /// by roughly the subject count, so anything under that catches the regression; four leaves room
    /// for a loaded continuous integration machine to be slow in ways that are not proportional to the
    /// number of subjects. An absolute threshold would be flaky here and would pin the delay constant.
    /// The ratio does not pin that constant either, but it does depend on it: see the remarks above.
    /// </summary>
    private const int ToleratedRatio = 4;

    [Fact]
    public async Task WhenManySubjectsEnterTheGraphTogether_ThenTheirStartsDoNotSerialize()
    {
        // Arrange
        var builder = Host.CreateApplicationBuilder();
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance()
            .WithHostedServices(builder.Services);

        var host = builder.Build();
        await host.StartAsync();

        try
        {
            // Warm the path once so neither measurement carries first call costs.
            await MeasureAttachAsync(context, 1);

            // Act
            var one = await MeasureAttachAsync(context, 1);
            var many = await MeasureAttachAsync(context, ManySubjects);

            // Assert
            Assert.True(
                many <= one * ToleratedRatio,
                $"Starting {ManySubjects} subjects took {many.TotalMilliseconds:F0} ms against " +
                $"{one.TotalMilliseconds:F0} ms for one, a ratio of {many / one:F1}. Starts that overlap " +
                $"stay near a ratio of one; a ratio near {ManySubjects} means they are running in series.");
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
        }
    }

    private static async Task<TimeSpan> MeasureAttachAsync(IInterceptorSubjectContext context, int subjectCount)
    {
        var graph = new StartupShapeGraph(context);
        var started = new CountdownEvent(subjectCount);

        var children = new StartupShapeSubject[subjectCount];
        for (var index = 0; index < subjectCount; index++)
        {
            children[index] = new StartupShapeSubject { Started = started };
        }

        var stopwatch = Stopwatch.StartNew();
        graph.Children = children;
        await Task.Run(() => started.Wait(TimeSpan.FromSeconds(30)));
        stopwatch.Stop();

        started.Dispose();
        graph.Children = null;

        return stopwatch.Elapsed;
    }
}

[InterceptorSubject]
public partial class StartupShapeGraph
{
    public partial StartupShapeSubject[]? Children { get; set; }
}

[InterceptorSubject]
public partial class StartupShapeSubject : IHostedService
{
    public partial string? Name { get; set; }

    public CountdownEvent? Started { get; set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Started?.Signal();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
