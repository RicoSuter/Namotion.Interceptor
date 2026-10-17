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
        var context = InterceptorSubjectContext.Create()
            .WithFullPropertyTracking()
            .WithRegistry()
            .WithLifecycle()
            .WithService<IPropertyLifecycleHandler>(
                () => new PropertyAttributeInitializer(),
                handler => handler is PropertyAttributeInitializer);
        var subject = new ChartTargetSubject(context);
        var property = subject.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        var viewer = new PropertyHistoryViewer(null!, new SubjectPathResolver(() => subject), context);

        // Act
        var canShow = viewer.CanShow(property);

        // Assert
        Assert.Equal(expected, canShow);
    }
}
