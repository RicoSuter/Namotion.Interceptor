namespace Namotion.Interceptor.Tracking.Tests;

public class StartupCompletionExtensionsTests
{
    [Fact]
    public void WhenContextHasNoStartupCompletion_ThenSharedHandleIsReturned()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var otherContext = InterceptorSubjectContext.Create();

        // Act
        var deferral = context.DeferStartupCompletion();
        var otherDeferral = otherContext.DeferStartupCompletion();
        deferral.Dispose();
        deferral.Dispose();

        // Assert
        Assert.Same(deferral, otherDeferral);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void WhenDeferring_ThenEveryRegisteredCompletionIsDeferredUntilDisposed(int count)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var completions = Enumerable.Range(0, count).Select(_ => new CountingCompletion()).ToArray();
        foreach (var completion in completions)
        {
            context.AddService<IStartupCompletion>(completion);
        }

        // Act
        var deferral = context.DeferStartupCompletion();

        // Assert
        Assert.All(completions, completion =>
        {
            Assert.Equal(1, completion.Deferred);
            Assert.Equal(0, completion.Released);
        });

        deferral.Dispose();
        Assert.All(completions, completion => Assert.Equal(1, completion.Released));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task WhenDisposedRepeatedlyAndConcurrently_ThenEachDeferralIsReleasedOnce(int count)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var completions = Enumerable.Range(0, count).Select(_ => new CountingCompletion()).ToArray();
        foreach (var completion in completions)
        {
            context.AddService<IStartupCompletion>(completion);
        }

        var deferral = context.DeferStartupCompletion();

        // Act
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(deferral.Dispose)));
        deferral.Dispose();

        // Assert
        Assert.All(completions, completion => Assert.Equal(1, completion.Released));
    }

    [Fact]
    public void WhenADeferThrows_ThenTheDeferralsAlreadyTakenAreReleasedAndTheExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var first = new CountingCompletion();
        var second = new CountingCompletion();
        var failure = new InvalidOperationException("Defer failed.");
        var last = new CountingCompletion();
        context.AddService<IStartupCompletion>(first);
        context.AddService<IStartupCompletion>(second);
        context.AddService<IStartupCompletion>(new ThrowingCompletion(failure));
        context.AddService<IStartupCompletion>(last);

        // Act & Assert
        var exception = Assert.Throws<InvalidOperationException>(() => context.DeferStartupCompletion());
        Assert.Same(failure, exception);
        Assert.Equal(1, first.Released);
        Assert.Equal(1, second.Released);
        Assert.Equal(0, last.Deferred);
    }

    [Fact]
    public void WhenADeferThrowsAndReleasingATakenDeferralThrows_ThenAnAggregateWithTheDeferFailureFirstPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var releaseFailure = new InvalidOperationException("Release failed.");
        var deferFailure = new InvalidOperationException("Defer failed.");
        var first = new CountingCompletion();
        var releaseThrowing = new CountingCompletion(releaseFailure);
        context.AddService<IStartupCompletion>(first);
        context.AddService<IStartupCompletion>(releaseThrowing);
        context.AddService<IStartupCompletion>(new ThrowingCompletion(deferFailure));

        // Act & Assert
        var exception = Assert.Throws<AggregateException>(() => context.DeferStartupCompletion());
        Assert.Equal([deferFailure, releaseFailure], exception.InnerExceptions);
        Assert.Equal(1, first.Released);
        Assert.Equal(1, releaseThrowing.Released);
    }

    [Fact]
    public void WhenReleasingOneDeferralThrows_ThenTheOthersAreStillReleasedAndTheExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var failure = new InvalidOperationException("Release failed.");
        var first = new CountingCompletion();
        var last = new CountingCompletion();
        context.AddService<IStartupCompletion>(first);
        context.AddService<IStartupCompletion>(new CountingCompletion(failure));
        context.AddService<IStartupCompletion>(last);
        var deferral = context.DeferStartupCompletion();

        // Act
        var exception = Assert.Throws<InvalidOperationException>(deferral.Dispose);
        deferral.Dispose();

        // Assert
        Assert.Same(failure, exception);
        Assert.Equal(1, first.Released);
        Assert.Equal(1, last.Released);
    }

    private sealed class CountingCompletion(Exception? releaseFailure = null) : IStartupCompletion
    {
        private readonly Exception? _releaseFailure = releaseFailure;
        private int _deferred;
        private int _released;

        public int Deferred => Volatile.Read(ref _deferred);

        public int Released => Volatile.Read(ref _released);

        public IDisposable Defer()
        {
            Interlocked.Increment(ref _deferred);
            return new Release(this);
        }

        private void OnReleased()
        {
            Interlocked.Increment(ref _released);
            if (_releaseFailure is not null)
            {
                throw _releaseFailure;
            }
        }

        private sealed class Release(CountingCompletion completion) : IDisposable
        {
            public void Dispose() => completion.OnReleased();
        }
    }

    private sealed class ThrowingCompletion(Exception failure) : IStartupCompletion
    {
        public IDisposable Defer() => throw failure;
    }
}
