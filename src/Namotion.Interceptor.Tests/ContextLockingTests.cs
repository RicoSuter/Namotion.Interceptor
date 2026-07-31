namespace Namotion.Interceptor.Tests;

public class ContextLockingTests
{
    private const int Attempts = 200;

    [Fact]
    public async Task WhenServicesAreAddedToFallbackWhileSubjectIsWritten_ThenNoDeadlockOccurs()
    {
        // Arrange: the subject context keeps an own service so that it maintains an own service
        // cache and walks into the fallback context instead of delegating to it.
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            var fallbackContext = InterceptorSubjectContext.Create();
            var subjectContext = InterceptorSubjectContext.Create();
            subjectContext.AddService(new MarkerService());
            subjectContext.AddFallbackContext(fallbackContext);

            var car = new Car(subjectContext);
            using var start = new ManualResetEventSlim(false);

            var writer = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < 2_000; index++)
                {
                    car.Speed = index;
                }
            }, TaskCreationOptions.LongRunning);

            var mutator = Task.Factory.StartNew(() =>
            {
                start.Wait();
                for (var index = 0; index < 200; index++)
                {
                    fallbackContext.AddService(new MarkerService());
                }
            }, TaskCreationOptions.LongRunning);

            // Act
            start.Set();
            var both = Task.WhenAll(writer, mutator);
            var finished = await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(15)));

            // Assert
            Assert.True(ReferenceEquals(finished, both),
                $"Deadlock on attempt {attempt} of {Attempts}: writing a property and adding a service " +
                "to a fallback context acquired the two context locks in opposite orders.");
        }
    }

    [Fact]
    public async Task WhenTwoThreadsTryAddTheSameServiceOnDelegatingContext_ThenOnlyOneSucceeds()
    {
        // Arrange: a context that delegates all service lookups to a single fallback context,
        // which is the state in which the delegation fast-path field is set.
        const int ConcurrentAttempts = 3_000;
        var violations = 0;

        for (var attempt = 1; attempt <= ConcurrentAttempts; attempt++)
        {
            var fallbackContext = InterceptorSubjectContext.Create();
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(fallbackContext);

            using var start = new Barrier(2);
            var results = new bool[2];

            var adders = new[]
            {
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[0] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning),
                Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    results[1] = context.TryAddService(() => new MarkerService(), _ => true);
                }, TaskCreationOptions.LongRunning)
            };

            // Act
            await Task.WhenAll(adders);

            // Assert
            if (results[0] == results[1])
            {
                violations++;
            }
        }

        Assert.True(violations == 0,
            $"TryAddService was not atomic in {violations} of {ConcurrentAttempts} attempts: two concurrent " +
            "calls for the same service type must have exactly one winner.");
    }

    private sealed class MarkerService;
}
