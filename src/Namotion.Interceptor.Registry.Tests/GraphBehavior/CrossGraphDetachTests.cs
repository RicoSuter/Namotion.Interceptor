using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;

namespace Namotion.Interceptor.Registry.Tests.GraphBehavior;

/// <summary>
/// A subject constructed with the context of one graph and placed into another graph leaves the
/// other graph without touching the graph it was constructed in.
/// </summary>
public class CrossGraphDetachTests
{
    [Fact]
    public void WhenRootOfOneGraphLeavesAnotherGraph_ThenItStaysInItsOwnGraph()
    {
        // Arrange
        var contextA = InterceptorSubjectContext.Create().WithRegistry();
        var contextB = InterceptorSubjectContext.Create().WithRegistry();
        var registryA = contextA.GetService<ISubjectRegistry>();

        var rootA = new Person(contextA) { FirstName = "A" };
        var rootB = new Person(contextB) { FirstName = "B" };
        rootB.Mother = rootA;

        // Act
        rootB.Mother = null;

        // Assert
        Assert.Contains(rootA, registryA.KnownSubjects.Keys);
        Assert.True(((InterceptorSubjectContext)((IInterceptorSubject)rootA).Context).HasFallbackContext(contextA));
    }

    [Fact]
    public async Task WhenSubjectsOfTwoGraphsLeaveEachOthersGraphConcurrently_ThenNeitherDeadlocks()
    {
        // Arrange
        const int iterations = 20000;
        var contextA = InterceptorSubjectContext.Create().WithRegistry();
        var contextB = InterceptorSubjectContext.Create().WithRegistry();
        var rootA = new Person(contextA);
        var rootB = new Person(contextB);

        // Act
        var first = Task.Factory.StartNew(() =>
        {
            for (var index = 0; index < iterations; index++)
            {
                var subject = new Person(contextA);
                rootB.Mother = subject;
                rootB.Mother = null;
            }
        }, TaskCreationOptions.LongRunning);

        var second = Task.Factory.StartNew(() =>
        {
            for (var index = 0; index < iterations; index++)
            {
                var subject = new Person(contextB);
                rootA.Father = subject;
                rootA.Father = null;
            }
        }, TaskCreationOptions.LongRunning);

        // Assert
        // A deadlock leaves both threads blocked for good, so the wait times out instead of hanging the run.
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(30));
    }
}
