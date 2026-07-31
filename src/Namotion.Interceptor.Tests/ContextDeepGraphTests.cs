namespace Namotion.Interceptor.Tests;

/// <summary>
/// A subject graph of depth N produces a context graph of depth N, because every attached child
/// inherits the context of its parent as a fallback context. Every walk over that graph therefore
/// has to be iterative: a recursive one dies on an uncatchable <see cref="StackOverflowException"/>
/// long before a legitimate graph runs out of memory, and no handler can save the process from it.
/// </summary>
public class ContextDeepGraphTests
{
    // Deep enough that the recursion this replaced dies on it. That version overflowed at roughly
    // 75,000 frames on an 8 MB stack and far earlier on the 1 MB stacks some hosts use, so a few
    // hundred levels would pass either way and prove nothing.
    private const int ChainLength = 100_000;

    /// <summary>
    /// Isolates the invalidation walk: every context on the chain delegates, so none of them holds
    /// a cache of its own and the service walk never leaves the root. The only walk that goes deep
    /// here is the one from the root up through 100,000 using sets.
    /// </summary>
    [Fact]
    public void WhenServiceIsAddedAtRootOfVeryDeepChain_ThenTheMutationCompletes()
    {
        // Arrange: one context per level, each one using the level below it as its only fallback
        // context, which puts it into the using set of that level.
        var rootContext = InterceptorSubjectContext.Create();
        rootContext.AddService(new MarkerService());

        InterceptorSubjectContext? middleContext = null;
        var deepestContext = rootContext;
        for (var index = 0; index < ChainLength; index++)
        {
            var context = InterceptorSubjectContext.Create();
            context.AddFallbackContext(deepestContext);
            deepestContext = context;

            if (index == ChainLength / 2)
            {
                middleContext = context;
            }
        }

        Assert.Single(deepestContext.GetServices<MarkerService>());

        // Act
        rootContext.AddService(new OtherMarkerService());

        // Assert
        Assert.Single(rootContext.GetServices<OtherMarkerService>());
        Assert.Single(middleContext!.GetServices<OtherMarkerService>());
        Assert.Single(deepestContext.GetServices<OtherMarkerService>());
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;
}
