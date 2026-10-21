using System.Reflection;
using System.Runtime.InteropServices;
using Namotion.Interceptor.Testing;

namespace Namotion.Interceptor.Tests.Context;

public class ContextServiceCacheTests
{
    private static readonly MethodInfo GetServiceArrayMethod = typeof(ContextServiceCacheTests)
        .GetMethod(nameof(GetServiceArray), BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"{nameof(GetServiceArray)} was renamed, this test needs updating.");

    [Fact]
    public async Task WhenManyServiceTypesAreQueriedConcurrently_ThenEveryCallerOfATypeGetsTheSameArray()
    {
        // Arrange: the first round hands out the type indices while the callers race, so the cache grows
        // while other callers fill it. Later rounds race the fills alone, each on a fresh context. Each
        // type has one service, so its array is not the shared empty one.
        const int serviceTypeCount = 32;
        const int callerCount = 8;
        const int roundCount = 16;
        var serviceTypes = ContextFunctionCacheTests.CreateNestedGenericTypes(
            serviceTypeCount, typeof(RacedServiceType<>), typeof(RacedServiceTypeRoot));
        var getServiceArrays = serviceTypes
            .Select(serviceType => GetServiceArrayMethod
                .MakeGenericMethod(serviceType)
                .CreateDelegate<Func<IInterceptorSubjectContext, object>>())
            .ToArray();

        for (var round = 0; round < roundCount; round++)
        {
            var context = InterceptorSubjectContext.Create();
            var services = serviceTypes.Select(serviceType => Activator.CreateInstance(serviceType)!).ToArray();
            foreach (var service in services)
            {
                context.AddService(service);
            }

            var results = new object[callerCount, serviceTypeCount];
            using var start = new Barrier(callerCount);
            var callers = Enumerable
                .Range(0, callerCount)
                .Select(callerIndex => Task.Factory.StartNew(() =>
                {
                    start.SignalAndWait();
                    for (var typeIndex = 0; typeIndex < serviceTypeCount; typeIndex++)
                    {
                        results[callerIndex, typeIndex] = getServiceArrays[typeIndex](context);
                    }
                }, TaskCreationOptions.LongRunning))
                .ToArray();

            // Act
            await AsyncTestHelpers.WaitUntilAsync(() => callers.All(caller => caller.IsCompleted),
                message: "Concurrent service queries did not finish");
            await Task.WhenAll(callers);

            // Assert: a filled entry is never lost to a concurrent fill or growth, so no caller rebuilt an
            // array another caller already received.
            for (var typeIndex = 0; typeIndex < serviceTypeCount; typeIndex++)
            {
                var first = (Array)results[0, typeIndex];
                Assert.Same(services[typeIndex], Assert.Single(first.Cast<object>()));
                for (var callerIndex = 1; callerIndex < callerCount; callerIndex++)
                {
                    Assert.Same(first, results[callerIndex, typeIndex]);
                }
            }
        }
    }

    [Fact]
    public void WhenNoServiceMatches_ThenContextsShareOneEmptyArray()
    {
        // Arrange: an empty array is the one result callers must not key caches on, because it is shared.
        var firstContext = InterceptorSubjectContext.Create();
        firstContext.AddService(new MarkerService());
        var secondContext = InterceptorSubjectContext.Create();
        secondContext.AddService(new MarkerService());

        // Act
        var firstServices = firstContext.GetServices<OtherMarkerService>();
        var secondServices = secondContext.GetServices<OtherMarkerService>();

        // Assert
        Assert.Empty(firstServices);
        Assert.Same(ImmutableCollectionsMarshal.AsArray(firstServices), ImmutableCollectionsMarshal.AsArray(secondServices));
    }

    [Theory]
    [InlineData(nameof(IInterceptorSubjectContext.AddService))]
    [InlineData(nameof(IInterceptorSubjectContext.TryAddService))]
    [InlineData(nameof(IInterceptorSubjectContext.AddFallbackContext))]
    [InlineData(nameof(IInterceptorSubjectContext.RemoveFallbackContext))]
    public void WhenFallbackOfFallbackChanges_ThenContextsWithOwnServicesResolveTheChange(string mutation)
    {
        // Arrange: every level has a service of its own, so none delegates and each keeps its own cache
        // for two service types that the mutation below must both refresh.
        var bottom = InterceptorSubjectContext.Create();
        bottom.AddService(new MarkerService());
        var middle = InterceptorSubjectContext.Create();
        middle.AddService(new MarkerService());
        middle.AddFallbackContext(bottom);
        var top = InterceptorSubjectContext.Create();
        top.AddService(new MarkerService());
        top.AddFallbackContext(middle);

        var extra = InterceptorSubjectContext.Create();
        extra.AddService(new MarkerService());
        extra.AddService(new OtherMarkerService());

        Assert.Equal(3, top.GetServices<MarkerService>().Length);
        Assert.Empty(top.GetServices<OtherMarkerService>());
        Assert.Equal(2, middle.GetServices<MarkerService>().Length);

        // Act
        int expectedMarkers;
        int expectedOtherMarkers;
        switch (mutation)
        {
            case nameof(IInterceptorSubjectContext.AddService):
                bottom.AddService(new OtherMarkerService());
                (expectedMarkers, expectedOtherMarkers) = (3, 1);
                break;
            case nameof(IInterceptorSubjectContext.TryAddService):
                Assert.True(bottom.TryAddService(() => new OtherMarkerService(), _ => true));
                (expectedMarkers, expectedOtherMarkers) = (3, 1);
                break;
            case nameof(IInterceptorSubjectContext.AddFallbackContext):
                Assert.True(bottom.AddFallbackContext(extra));
                (expectedMarkers, expectedOtherMarkers) = (4, 1);
                break;
            default:
                Assert.True(middle.RemoveFallbackContext(bottom));
                (expectedMarkers, expectedOtherMarkers) = (2, 0);
                break;
        }

        // Assert
        Assert.Equal(expectedMarkers, top.GetServices<MarkerService>().Length);
        Assert.Equal(expectedOtherMarkers, top.GetServices<OtherMarkerService>().Length);
        Assert.Equal(expectedMarkers - 1, middle.GetServices<MarkerService>().Length);
        Assert.Equal(expectedOtherMarkers, middle.GetServices<OtherMarkerService>().Length);
    }

    private static object GetServiceArray<TService>(IInterceptorSubjectContext context)
    {
        return ImmutableCollectionsMarshal.AsArray(context.GetServices<TService>())!;
    }

    private sealed class MarkerService;

    private sealed class OtherMarkerService;

    private sealed class RacedServiceType<TService>;

    private sealed class RacedServiceTypeRoot;
}
