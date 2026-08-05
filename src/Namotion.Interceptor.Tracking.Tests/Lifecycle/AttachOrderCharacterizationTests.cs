using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Parent;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Pins the traversal orders and the resolution facts that the context inheritance redesign is
/// required to leave bit-identical. Every test here must pass against unmodified master: a failure
/// means the test is wrong, not the production code.
/// </summary>
public class AttachOrderCharacterizationTests
{
    [Fact]
    public void WhenThreeLevelGraphIsAttached_ThenBothChannelsObserveTheDocumentedOrder()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        var events = new List<string>();
        lifecycleInterceptor.SubjectAttached += change => events.Add($"EVENT.attached({Name(change.Subject)})");
        lifecycleInterceptor.SubjectDetaching += change => events.Add($"EVENT.detaching({Name(change.Subject)})");

        var handlerLog = new List<string>();
        context.WithService(() => new RecordingLifecycleHandler(handlerLog));

        var m1 = new Person(context) { FirstName = "M1" };
        var m3 = new Person { FirstName = "M3" };
        var m2 = new Person { FirstName = "M2", Mother = m3 };

        // m1's own constructor attach already raised SubjectAttached, so the channels start dirty.
        events.Clear();
        handlerLog.Clear();

        // Act
        m1.Mother = m2;
        var attachEvents = events.ToArray();
        var attachHandlerLog = handlerLog.ToArray();

        events.Clear();
        handlerLog.Clear();
        m1.Mother = null;

        // Assert
        // The recording handler is registered after WithContextInheritance and carries no ordering
        // attribute, so it resolves BEHIND the inheritance handler. The inheritance handler's
        // descent therefore attaches M3 synchronously before the recorder ever sees M2, which is
        // the bottom-up order spec section 2 measured for an after-inheritance handler.
        Assert.Equal(["EVENT.attached(M3)", "EVENT.attached(M2)"], attachEvents);
        Assert.Equal(["handler.att(M3)", "handler.att(M2)"], attachHandlerLog);

        Assert.Equal(["EVENT.detaching(M2)", "EVENT.detaching(M3)"], events);
        Assert.Equal(["handler.det(M3)", "handler.det(M2)"], handlerLog);
    }

    [Fact]
    public void WhenRootIsAttachedWithChildren_ThenTheRootsOwnAttachFiresLast()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;
        var attached = new List<string>();
        lifecycleInterceptor.SubjectAttached += change => attached.Add(Name(change.Subject));

        var child = new Person { FirstName = "Child" };
        var root = new Person { FirstName = "Root", Mother = child };

        // Act
        ((IInterceptorSubject)root).Context.AddFallbackContext(context);

        // Assert
        Assert.Equal(["Child", "Root"], attached);
    }

    [Fact]
    public void WhenRegistryIsRegisteredFirst_ThenItResolvesAheadOfContextInheritance()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithParents()
            .WithContextInheritance();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Equal(
            ["SubjectRegistry", "ParentTrackingHandler", "ContextInheritanceHandler"],
            handlers.Select(handler => handler.GetType().Name).ToArray());
    }

    [Fact]
    public void WhenRegistryIsRegisteredLast_ThenItResolvesBehindContextInheritance()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithParents()
            .WithContextInheritance()
            .WithRegistry();

        // Act
        var handlers = context.GetServices<ILifecycleHandler>();

        // Assert
        Assert.Equal(
            ["ParentTrackingHandler", "ContextInheritanceHandler", "SubjectRegistry"],
            handlers.Select(handler => handler.GetType().Name).ToArray());
    }

    [Fact]
    public void WhenHandlerRunsBeforeContextInheritance_ThenTheChildResolvesNothingYet()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithRegistry()
            .WithParents()
            .WithContextInheritance();

        var observations = new List<(string name, int registries, int lifecycles)>();
        context.WithService(() => new ProbeAheadOfInheritance(observations));

        var root = new Person(context) { FirstName = "Root" };
        var grandchild = new Person { FirstName = "Grandchild" };
        var child = new Person { FirstName = "Child", Mother = grandchild };

        // Act
        root.Mother = child;

        // Assert
        Assert.All(observations, observation =>
        {
            Assert.Equal(0, observation.registries);
            Assert.Equal(0, observation.lifecycles);
        });
        Assert.Contains(observations, observation => observation.name == "Child");
        Assert.Contains(observations, observation => observation.name == "Grandchild");
    }

    [Fact]
    public void WhenSubjectHasTwoParents_ThenItAttachesOnceAndDetachesOnce()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithContextInheritance();

        var lifecycleInterceptor = context.TryGetLifecycleInterceptor()!;

        // Subscribed after the parents exist: each constructor attach raises SubjectAttached, so
        // wiring the counters first would start them at two.
        var parent1 = new Person(context) { FirstName = "P1" };
        var parent2 = new Person(context) { FirstName = "P2" };
        var shared = new Person { FirstName = "Shared" };

        var attachCount = 0;
        var detachCount = 0;
        lifecycleInterceptor.SubjectAttached += _ => attachCount++;
        lifecycleInterceptor.SubjectDetaching += _ => detachCount++;

        // Act
        parent1.Mother = shared;
        parent2.Mother = shared;
        var attachesAfterBoth = attachCount;

        parent1.Mother = null;
        var detachesAfterFirstRemoval = detachCount;

        parent2.Mother = null;

        // Assert
        Assert.Equal(1, attachesAfterBoth);
        Assert.Equal(0, detachesAfterFirstRemoval);
        Assert.Equal(1, detachCount);
        Assert.Equal(0, shared.GetReferenceCount());
    }

    private static string Name(IInterceptorSubject subject)
    {
        return ((Person)subject).FirstName ?? "?";
    }

    private class RecordingLifecycleHandler(List<string> log) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.Property.HasValue)
            {
                return;
            }

            var prefix = change.IsPropertyReferenceAdded ? "att" : "det";
            log.Add($"handler.{prefix}({Name(change.Subject)})");
        }
    }

    [RunsBefore(typeof(ContextInheritanceHandler))]
    private class ProbeAheadOfInheritance(List<(string, int, int)> observations) : ILifecycleHandler
    {
        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (!change.IsPropertyReferenceAdded)
            {
                return;
            }

            observations.Add((
                Name(change.Subject),
                change.Subject.Context.GetServices<ISubjectRegistry>().Length,
                change.Subject.Context.GetServices<ILifecycleInterceptor>().Length));
        }
    }
}
