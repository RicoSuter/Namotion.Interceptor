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

    private sealed class MarkerService;
}
