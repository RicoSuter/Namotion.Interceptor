using System.Reactive.Concurrency;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Registry.Tests;

[InterceptorSubject]
public partial class RegistryReader
{
    public RegistryTarget? Target;

    [Derived]
    public bool IsTargetRegistered => Target?.TryGetRegisteredSubject() is not null;
}

[InterceptorSubject]
public partial class RegistryTarget
{
    public partial int Value { get; set; }
}

[InterceptorSubject]
public partial class RegistryHolder
{
    public partial RegistryReader? Reader { get; set; }

    public partial RegistryTarget? Target { get; set; }
}

[InterceptorSubject]
public partial class StructureHolder
{
    public StructureHolder()
    {
        Targets = [];
    }

    public partial StructureReader? Reader { get; set; }

    public partial RegistryTarget? Target { get; set; }

    public partial RegistryTarget? OtherTarget { get; set; }

    public partial RegistryTarget[] Targets { get; set; }

    public partial StructureHolder? Child { get; set; }
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
    [Fact]
    public void WhenSubjectReadThroughRegistryIsAttachedLater_ThenDerivedPropertyPublishesTheChange()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var holder = new RegistryHolder(context);
        var target = new RegistryTarget();
        holder.Reader = new RegistryReader { Target = target };
        var changes = new List<SubjectPropertyChange>();
        context.GetPropertyChangeObservable(ImmediateScheduler.Instance).Subscribe(changes.Add);

        // Act
        holder.Target = target;

        // Assert
        var change = Assert.Single(changes, c => c.Property.Subject == holder.Reader);
        Assert.False(change.GetOldValue<bool>());
        Assert.True(change.GetNewValue<bool>());
    }

    [Theory]
    [InlineData("ISubjectRegistry.TryGetRegisteredSubject")]
    [InlineData("RegisteredSubject.Parents")]
    [InlineData("RegisteredSubject.ReferenceCount")]
    [InlineData("GetReferenceCount")]
    [InlineData("RegisteredSubject.Properties")]
    [InlineData("RegisteredSubject.TryGetProperty")]
    [InlineData("RegisteredSubject.GetPropertyAttributes")]
    [InlineData("RegisteredSubject.TryGetPropertyAttribute")]
    [InlineData("RegisteredSubjectProperty.Children")]
    [InlineData("MovedChild")]
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
    public void WhenGetterSideEffectRunsReconciliation_ThenRegistryReadsAreNotRecorded()
    {
        // Arrange: each evaluation assigns a new target to another holder, whose reconciliation looks up the
        // holder's registered properties on the evaluating thread.
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

        // Act
        other.TryGetRegisteredSubject()!.AddProperty<int>("Added", _ => 1);

        // Assert
        Assert.Equal(1, evaluations);
        Assert.Empty(new PropertyReference(root.Reader, nameof(StructureReader.Value)).GetRequiredProperties().ToArray());
    }

    private static (Func<string?> Read, Action Change) CreateScenario(
        string api, IInterceptorSubjectContext context, StructureHolder root, RegistryTarget target)
    {
        if (api == "ISubjectRegistry.TryGetRegisteredSubject")
        {
            var registry = context.GetService<ISubjectRegistry>();
            return (() => (registry.TryGetRegisteredSubject(target) is not null).ToString(), () => root.Target = target);
        }

        if (api == "RegisteredSubjectProperty.Children")
        {
            // The registry clears the children of a property when its subject detaches, without a write of the property.
            var child = new StructureHolder { Targets = [target] };
            root.Child = child;
            var targets = child.TryGetRegisteredSubject()!.TryGetProperty(nameof(StructureHolder.Targets))!;
            return (() => targets.Children.Length.ToString(), () => root.Child = null);
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
}
