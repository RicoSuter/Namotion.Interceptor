using HomeBlaze.History.Blazor;
using HomeBlaze.Services;
using HomeBlaze.Services.Lifecycle;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Xunit;

namespace HomeBlaze.History.Blazor.Tests;

public class PropertyHistoryViewerTests
{
    [Theory]
    [InlineData(nameof(ChartTargetSubject.Temperature), true)]
    [InlineData(nameof(ChartTargetSubject.IsOn), true)]
    [InlineData(nameof(ChartTargetSubject.Status), true)]
    [InlineData(nameof(ChartTargetSubject.Mode), true)]
    [InlineData(nameof(ChartTargetSubject.NotRecorded), false)]
    [InlineData(nameof(ChartTargetSubject.Child), false)]
    public void WhenPropertyIsAsked_ThenItCanBeShownOnlyWhenItIsARecordedState(string propertyName, bool expected)
    {
        // Arrange
        var context = CreateContext();
        var subject = new ChartTargetSubject(context);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        var viewer = new PropertyHistoryViewer(null!, new SubjectPathResolver(() => subject), context);

        // Act
        var canShow = viewer.CanShow(property);

        // Assert
        Assert.Equal(expected, canShow);
    }

    [Fact]
    public void WhenSubjectOfThePropertyHasNoPath_ThenItsHistoryCannotBeShown()
    {
        // Arrange: the subject is not in the graph below the root, so its history has no path to query.
        var context = CreateContext();
        var root = new ChartTargetSubject(context);
        var detached = new ChartTargetSubject(context);
        var property = detached.TryGetRegisteredSubject()!.TryGetProperty(nameof(ChartTargetSubject.Temperature))!;
        var viewer = new PropertyHistoryViewer(null!, new SubjectPathResolver(() => root), context);

        // Act
        var canShow = viewer.CanShow(property);

        // Assert
        Assert.False(canShow);
    }

    private static IInterceptorSubjectContext CreateContext() =>
        InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
}
