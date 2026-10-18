using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Blazor;
using static Namotion.Interceptor.Blazor.Tests.TrackingScopeTests;

namespace Namotion.Interceptor.Blazor.Tests;

internal static class ComponentTrackingTestHelpers
{
    public static TestRenderer CreateRenderer(TestRenderMode mode, int throttleMilliseconds = 0, int maximumUnacknowledgedBatches = 10)
    {
        return new TestRenderer(mode, maximumUnacknowledgedBatches, services => services
            .AddBlazorSubjectTracking(options => options.ThrottleMilliseconds = throttleMilliseconds));
    }

    /// <summary>Waits until no change waits for a drain and the client has acknowledged every batch.</summary>
    public static Task SettleAsync(TestRenderer renderer)
    {
        var dispatcher = renderer.Services.GetRequiredService<ComponentTrackingDispatcher>();
        return WaitAsync(() => dispatcher.IsIdle && renderer.UnacknowledgedBatchCount == 0);
    }
}
