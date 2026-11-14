using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

[InterceptorSubject]
public partial class RegistryTarget
{
    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class StructureHolder
{
    public StructureHolder()
    {
        Targets = [];
        TargetsByKey = [];
    }

    public partial StructureReader? Reader { get; set; }

    public partial RegistryTarget? Target { get; set; }

    public partial RegistryTarget? OtherTarget { get; set; }

    public partial RegistryTarget[] Targets { get; set; }

    public partial StructureHolder? Child { get; set; }

    public partial Dictionary<string, RegistryTarget> TargetsByKey { get; set; }
}

/// <summary>
/// Evaluates its derived property with a delegate, so that a test decides what the getter reads and does.
/// </summary>
[InterceptorSubject]
public partial class StructureReader
{
    public Func<string?>? Read;

    [Derived]
    public string? Value => Read?.Invoke();
}

public class DerivedRegistryStructureTests
{
    [Theory]
    [InlineData("TryGetRegisteredSubject")]
    [InlineData("ISubjectRegistry.TryGetRegisteredSubject")]
    [InlineData("TryGetRegisteredProperty")]
    [InlineData("TryGetRegisteredProperty(expression)")]
    [InlineData("RegisteredSubject.Parents")]
    [InlineData("RegisteredSubject.ReferenceCount")]
    [InlineData("GetReferenceCount")]
    [InlineData("RegisteredSubject.Properties")]
    [InlineData("RegisteredSubject.TryGetProperty")]
    [InlineData("RegisteredSubject.GetPropertyAttributes")]
    [InlineData("RegisteredSubject.TryGetPropertyAttribute")]
    [InlineData("RegisteredSubjectProperty.Attributes")]
    [InlineData("RegisteredSubjectProperty.Children")]
    [InlineData("MovedChild")]
    [InlineData("RekeyedChild")]
    [InlineData("ContextDetach")]
    [InlineData("AddPropertyUnderLifecycleLock")]
    public void WhenStructureReadThroughApiChanges_ThenDerivedPropertyPublishesTheChange(string api)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var target = new RegistryTarget();
        var (read, change) = CreateScenario(api, context, root, target);
        root.Reader = new StructureReader { Read = read };
        var initialValue = root.Reader.Value;
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        change();

        // Assert
        var published = Assert.Single(changes, c => c.Property.Subject == root.Reader);
        Assert.Equal(initialValue, published.GetOldValue<string?>());
        Assert.Equal(read(), published.GetNewValue<string?>());
        Assert.NotEqual(initialValue, read());
    }

    [Fact]
    public void WhenSubtreeIsAttached_ThenDerivedPropertyInItFollowsTheChildrenAttachedAfterIt()
    {
        // Arrange: the reader attaches before the targets of its holder, and reads the registered children.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var child = new StructureHolder { Targets = [new RegistryTarget(), new RegistryTarget()] };
        child.Reader = new StructureReader
        {
            Read = () => child.TryGetRegisteredSubject()?.TryGetProperty(nameof(StructureHolder.Targets))?.Children.Length.ToString()
        };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        root.Child = child;

        // Assert
        var published = Assert.Single(changes, c => c.Property.Subject == child.Reader);
        Assert.Equal("0", published.GetOldValue<string?>());
        Assert.Equal("2", published.GetNewValue<string?>());
    }

    [Fact]
    public void WhenSubtreeIsAttached_ThenDerivedPropertyInItThatReadsTheChildrenOfItsHolderIsEvaluatedOnce()
    {
        // Arrange: the reader attaches with the child, after the registry added the child to the root's children.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var evaluations = 0;
        var child = new StructureHolder
        {
            Reader = new StructureReader
            {
                Read = () =>
                {
                    evaluations++;
                    return root.TryGetRegisteredSubject()!.TryGetProperty(nameof(StructureHolder.Child))!.Children.Length.ToString();
                }
            }
        };

        // Act
        root.Child = child;

        // Assert
        Assert.Equal(1, evaluations);
        Assert.Equal("1", child.Reader.Value);
    }

    [Fact]
    public void WhenObserverOfAPropertyReaderThrows_ThenAddedDerivedPropertyIsAttachedAndTheExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var target = new RegistryTarget();
        root.Target = target;
        var registered = target.TryGetRegisteredSubject()!;
        root.Reader = new StructureReader { Read = () => registered.Properties.Length.ToString() };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root.Reader)
            {
                throw new InvalidOperationException();
            }
        });

        // Act
        var exception = Record.Exception(() => registered.AddDerivedProperty<int>("Doubled", _ => target.Value * 2));
        changes.Clear();
        target.Value = 5;

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        var change = Assert.Single(changes, c => c.Property.Name == "Doubled");
        Assert.Equal(10, change.GetNewValue<int>());
    }

    [Fact]
    public void WhenAddedPropertyAndAPropertyReaderBothThrow_ThenTheAddedPropertysExceptionPropagates()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var target = new RegistryTarget();
        root.Target = target;
        var registered = target.TryGetRegisteredSubject()!;
        root.Reader = new StructureReader { Read = () => registered.Properties.Length.ToString() };
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Name == "Doubled")
            {
                throw new ArgumentException("added property");
            }
        });
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(change =>
        {
            if (change.Property.Subject == root.Reader)
            {
                throw new InvalidOperationException("reader");
            }
        });

        // Act
        var exception = Record.Exception(() => registered.AddDerivedProperty<int>("Doubled", _ => target.Value * 2));

        // Assert
        Assert.IsType<ArgumentException>(exception);
        Assert.Equal(registered.Properties.Length.ToString(), root.Reader.Value);
    }

    [Theory]
    [InlineData("ContextAttach")]
    [InlineData("ContextDetach")]
    [InlineData("ReferenceRemoved")]
    [InlineData("MovedChild")]
    public void WhenHandlerAfterTheRegistryThrows_ThenDerivedPropertyFollowsTheStructureTheRegistryChanged(string operation)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var target = new RegistryTarget();
        var handler = new ThrowingHandler(target);
        context.WithService(() => handler);
        var targetContext = ((IInterceptorSubject)target).Context;
        Func<string?> read = () => (target.TryGetRegisteredSubject() is not null).ToString();
        Action act = operation switch
        {
            "ContextAttach" => () => targetContext.AddFallbackContext(context),
            "ContextDetach" => () => targetContext.RemoveFallbackContext(context),
            "ReferenceRemoved" => () => root.Target = null,
            "MovedChild" => () => root.Targets = [new RegistryTarget(), target],
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (operation == "ContextDetach")
        {
            targetContext.AddFallbackContext(context);
        }
        else if (operation == "ReferenceRemoved")
        {
            root.Target = target;
        }
        else if (operation == "MovedChild")
        {
            root.Targets = [target];
            var registered = target.TryGetRegisteredSubject()!;
            read = () => registered.Parents[0].Index?.ToString();
        }

        root.Reader = new StructureReader { Read = read };
        var initialValue = root.Reader.Value;
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);
        handler.IsArmed = true;

        // Act
        var exception = Record.Exception(act);

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        var published = Assert.Single(changes, c => c.Property.Subject == root.Reader);
        Assert.Equal(initialValue, published.GetOldValue<string?>());
        Assert.Equal(read(), published.GetNewValue<string?>());
        Assert.NotEqual(initialValue, read());
    }

    [Fact]
    public void WhenGetterSideEffectRunsReconciliation_ThenRegistryReadsAreNotRecorded()
    {
        // Arrange: each evaluation assigns a new target to another holder, whose reconciliation looks up the
        // holder's registered properties and registers the target on the evaluating thread.
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var root = new StructureHolder(context);
        var other = new StructureHolder(context);
        var evaluations = 0;
        root.Reader = new StructureReader
        {
            Read = () =>
            {
                evaluations++;
                other.Target = new RegistryTarget();
                return "value";
            }
        };

        // Act: the structure of the holder, then of the target changes.
        other.TryGetRegisteredSubject()!.AddProperty<int>("Added", _ => 1);
        other.OtherTarget = other.Target;

        // Assert
        Assert.Equal(1, evaluations);
    }

    private static (Func<string?> Read, Action Change) CreateScenario(
        string api, IInterceptorSubjectContext context, StructureHolder root, RegistryTarget target)
    {
        if (api is "TryGetRegisteredSubject" or "ISubjectRegistry.TryGetRegisteredSubject")
        {
            // The target is reached without a tracked property and has no registry until it is attached.
            var registry = context.GetService<ISubjectRegistry>();
            Func<RegisteredSubject?> getRegistered = api == "TryGetRegisteredSubject"
                ? target.TryGetRegisteredSubject
                : () => registry.TryGetRegisteredSubject(target);
            return (() => (getRegistered() is not null).ToString(), () => root.Target = target);
        }

        if (api == "TryGetRegisteredProperty")
        {
            return (() => (target.TryGetRegisteredProperty(nameof(RegistryTarget.Value)) is not null).ToString(), () => root.Target = target);
        }

        if (api == "TryGetRegisteredProperty(expression)")
        {
            return (() => (target.TryGetRegisteredProperty(t => t.Value) is not null).ToString(), () => root.Target = target);
        }

        if (api == "RegisteredSubjectProperty.Children")
        {
            // The registry clears the children of a property when its subject detaches, without a write of the property.
            var child = new StructureHolder { Targets = [target] };
            root.Child = child;
            var targets = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(StructureHolder.Targets))!;
            return (() => targets.Children.Length.ToString(), () => root.Child = null);
        }

        if (api == "RekeyedChild")
        {
            root.TargetsByKey = new Dictionary<string, RegistryTarget> { ["first"] = target };
            var parents = target.TryGetRegisteredSubject()!;
            return (() => parents.Parents[0].Index?.ToString(), () => root.TargetsByKey = new Dictionary<string, RegistryTarget> { ["second"] = target });
        }

        if (api == "ContextDetach")
        {
            ((IInterceptorSubject)target).Context.AddFallbackContext(context);
            return (() => (target.TryGetRegisteredSubject() is not null).ToString(), () => ((IInterceptorSubject)target).Context.RemoveFallbackContext(context));
        }

        if (api == "MovedChild")
        {
            root.Targets = [target];
            var parents = target.TryGetRegisteredSubject()!;
            return (() => parents.Parents[0].Index?.ToString(), () => root.Targets = [new RegistryTarget(), target]);
        }

        root.Target = target;
        var registered = target.TryGetRegisteredSubject()!;
        Action addReference = () => root.OtherTarget = target;
        Action addProperty = () => registered.AddProperty<int>("Added", _ => 1);
        Action addAttribute = () => registered.TryGetProperty(nameof(RegistryTarget.Value))!.AddAttribute<int>("Unit", _ => 1);
        if (api == "AddPropertyUnderLifecycleLock")
        {
            var other = new RegistryTarget();
            context.GetService<LifecycleInterceptor>().SubjectAttached += change =>
            {
                if (change.Subject == other)
                {
                    registered.AddProperty<int>("Added", _ => 1);
                }
            };
            return (() => registered.Properties.Length.ToString(), () => root.OtherTarget = other);
        }

        if (api == "RegisteredSubjectProperty.Attributes")
        {
            // The attributes are cached before the getter first reads them.
            var property = registered.TryGetProperty(nameof(RegistryTarget.Value))!;
            _ = property.Attributes;
            return (() => property.Attributes.Length.ToString(), addAttribute);
        }

        return api switch
        {
            "RegisteredSubject.Parents" => (() => registered.Parents.Length.ToString(), addReference),
            "RegisteredSubject.ReferenceCount" => (() => registered.ReferenceCount.ToString(), addReference),
            "GetReferenceCount" => (() => target.GetReferenceCount().ToString(), addReference),
            "RegisteredSubject.Properties" => (() => registered.Properties.Length.ToString(), addProperty),
            "RegisteredSubject.TryGetProperty" => (() => (registered.TryGetProperty("Added") is not null).ToString(), addProperty),
            "RegisteredSubject.GetPropertyAttributes" => (() => registered.GetPropertyAttributes(nameof(RegistryTarget.Value)).Count().ToString(), addAttribute),
            "RegisteredSubject.TryGetPropertyAttribute" => (() => (registered.TryGetPropertyAttribute(nameof(RegistryTarget.Value), "Unit") is not null).ToString(), addAttribute),
            _ => throw new ArgumentOutOfRangeException(nameof(api))
        };
    }

    [RunsAfter(typeof(SubjectRegistry))]
    private sealed class ThrowingHandler(IInterceptorSubject subject) : ILifecycleHandler, IChildIndexHandler
    {
        public bool IsArmed { get; set; }

        public void HandleLifecycleChange(SubjectLifecycleChange change)
        {
            if (IsArmed && change.Subject == subject)
            {
                throw new InvalidOperationException();
            }
        }

        public void HandleChildIndexChanges(PropertyReference property, ReadOnlySpan<SubjectChild> children)
        {
            if (IsArmed)
            {
                throw new InvalidOperationException();
            }
        }
    }
}
