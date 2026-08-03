namespace Namotion.Interceptor.Dynamic.Tests;

/// <summary>
/// <see cref="DynamicSubject"/> publishes its executor with the same compare-and-swap the generator
/// emits, and for the same reason: a lazy assignment lets two threads racing the first access each
/// build an executor and discard one, along with the per-subject commit revision counter on it.
/// The generated path is covered by <c>ExecutorPublicationTests</c>; this pins the hand-written twin,
/// which is a separate copy of the same code and can drift independently.
/// </summary>
public class DynamicSubjectExecutorPublicationTests
{
    [Fact]
    public void WhenContextIsAccessedConcurrently_ThenAllThreadsSeeTheSameExecutor()
    {
        // Arrange: the parameterless constructor is the one that leaves the executor unpublished. The
        // context-taking overload resolves Context while constructing, so it could never race.
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var subject = new DynamicSubject();
            var contexts = new IInterceptorSubjectContext[2];
            using var start = new ManualResetEventSlim(false);
            var first = new Thread(() => { start.Wait(); contexts[0] = ((IInterceptorSubject)subject).Context; });
            var second = new Thread(() => { start.Wait(); contexts[1] = ((IInterceptorSubject)subject).Context; });
            first.Start();
            second.Start();

            // Act
            start.Set();
            first.Join();
            second.Join();

            // Assert
            Assert.Same(contexts[0], contexts[1]);
        }
    }
}
