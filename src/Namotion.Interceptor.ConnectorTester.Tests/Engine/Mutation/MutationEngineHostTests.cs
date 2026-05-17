using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Interceptor.ConnectorTester.Configuration;
using Namotion.Interceptor.ConnectorTester.Engine;
using Namotion.Interceptor.ConnectorTester.Engine.Mutation;
using Namotion.Interceptor.ConnectorTester.Model;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Testing;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.ConnectorTester.Tests.Engine.Mutation;

public class MutationEngineHostTests
{
    private static IInterceptorSubjectContext CreateContext()
        => InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithParents()
            .WithLifecycle();

    [Fact]
    public async Task WhenStructuralMutationRateIsZero_ThenOnlyValueMutationsRun()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration
        {
            Name = "test",
            ValueMutationRate = 100,
            StructuralMutationRate = 0
        };
        var host = MutationEngineHost.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        await host.StartAsync(CancellationToken.None);
        await AsyncTestHelpers.WaitUntilAsync(() => host.ValueMutationCount > 0,
            timeout: TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(20));
        await host.StopAsync(CancellationToken.None);

        // Assert
        Assert.True(host.ValueMutationCount > 0);
        Assert.Equal(0, host.StructuralMutationCount);
    }

    [Fact]
    public void WhenResetCountersCalled_ThenBothCountersZero()
    {
        // Arrange
        var context = CreateContext();
        var root = new TestNode(context);
        var coordinator = new TestCycleCoordinator();
        var configuration = new ParticipantConfiguration { Name = "test", ValueMutationRate = 50 };
        var host = MutationEngineHost.CreateRandom(root, configuration, coordinator, NullLogger.Instance);

        // Act
        host.ResetCounters();

        // Assert
        Assert.Equal(0, host.ValueMutationCount);
        Assert.Equal(0, host.StructuralMutationCount);
    }
}
