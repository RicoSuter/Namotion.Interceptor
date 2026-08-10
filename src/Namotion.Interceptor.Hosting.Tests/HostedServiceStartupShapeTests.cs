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
public class HostedServiceStartupShapeTests
{
    private const int ManySubjects = 32;

    /// <summary>
    /// Deliberately a ratio and deliberately generous. Serialized starts separate the two measurements
    /// by roughly the subject count, so anything under that catches the regression; four leaves room
    /// for a loaded continuous integration machine to be slow in ways that are not proportional to the
    /// number of subjects. An absolute threshold would be flaky here and would pin the delay constant,
    /// which is a workaround rather than a guarantee.
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
