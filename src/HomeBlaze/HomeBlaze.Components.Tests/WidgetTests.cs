using System.Collections.Concurrent;
using HomeBlaze.Components.Tests.Models;
using HomeBlaze.Services;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Recorder;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class WidgetTests
{
    [Fact]
    public void WhenWidgetIsReadForRendering_ThenOnlyItsResolvedSubjectIsRecorded()
    {
        // Arrange
        var (_, widget, motor, _) = CreateGraph();

        // Act
        var recordedProperties = new ConcurrentDictionary<PropertyReference, bool>();
        IInterceptorSubject? resolvedSubject;
        using (ReadPropertyRecorder.Start(recordedProperties))
        {
            resolvedSubject = widget.GetLastResolvedSubject();
        }

        // Assert
        Assert.Same(motor, resolvedSubject);
        Assert.Equal([new PropertyReference(widget, nameof(Widget.ResolvedSubject))], recordedProperties.Keys);
    }

    [Fact]
    public void WhenSubjectAtPathIsReplaced_ThenRenderingReadsTheNewSubject()
    {
        // Arrange
        var (devices, widget, _, _) = CreateGraph();

        // Act
        var replacement = new TestDevice { Name = "Replacement" };
        devices.Children = new Dictionary<string, IInterceptorSubject> { ["Motor1"] = replacement };

        // Assert
        Assert.Same(replacement, widget.GetLastResolvedSubject());
    }

    [Fact]
    public void WhenWidgetIsNotAttached_ThenRenderingResolvesThePath()
    {
        // Arrange
        var (_, _, motor, resolver) = CreateGraph();
        var widget = new Widget(resolver) { Path = "/Devices/Motor1" };

        // Act
        var resolvedSubject = widget.GetLastResolvedSubject();

        // Assert
        Assert.Same(motor, resolvedSubject);
    }

    private static (TestFolder Devices, Widget Widget, TestDevice Motor, SubjectPathResolver Resolver) CreateGraph()
    {
        TestFolder? root = null;
        var resolver = new SubjectPathResolver(() => root);
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithReadPropertyRecorder()
            .WithRegistry()
            .WithService<ILifecycleHandler>(() => resolver, handler => handler == resolver);

        root = new TestFolder(context);
        var motor = new TestDevice { Name = "Motor" };
        var devices = new TestFolder { Children = new Dictionary<string, IInterceptorSubject> { ["Motor1"] = motor } };
        var widget = new Widget(resolver) { Path = "/Devices/Motor1" };
        root.Children = new Dictionary<string, IInterceptorSubject>
        {
            ["Devices"] = devices,
            ["Dashboard"] = widget
        };

        return (devices, widget, motor, resolver);
    }
}
