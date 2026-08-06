using System.Collections.Concurrent;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Parent;

namespace Namotion.Interceptor.Registry.Tests;

/// <summary>
/// Covers the capability a derived attribute needs when its getter resolves ancestors through the
/// registry: the whole ancestor chain must be registered by the time the getter first runs.
///
/// This is a different entry point from <see cref="RegistryHandlerOrderTests"/>. There the observer
/// is an <see cref="Tracking.Lifecycle.ILifecycleHandler"/>. Here the attribute is created by an
/// <see cref="ISubjectPropertyInitializer"/> that the registry invokes from inside its own handler,
/// and the getter is evaluated by <c>DerivedPropertyChangeHandler.AttachProperty</c> when the
/// property attaches. See "Handler Order Around the Descent" in docs/design/tracking-lifecycle.md.
///
/// The assertions read what the getter computed on its FIRST evaluation, not the current value.
/// Reading the attribute afterwards re-resolves against the settled graph and would report a correct
/// chain either way, which is exactly the observation an attach-time ordering test must not make.
/// </summary>
public class RegistryAncestorResolutionTests
{
    private const string EnabledPropertyName = nameof(FlagNode.Enabled);
    private const string ResolvedAncestorsAttribute = "ResolvedAncestors";
    private const string InheritedEnabledAttribute = "InheritedEnabled";

    public static TheoryData<string> RegistrationOrders() =>
    [
        "registry-first",
        "registry-after-tracking",
        "registry-after-parents"
    ];

    private static (IInterceptorSubjectContext Context, InheritedFlagInitializer Initializer) CreateContext(string registrationOrder)
    {
        IInterceptorSubjectContext context = InterceptorSubjectContext.Create();
        context = registrationOrder switch
        {
            "registry-first" => context.WithRegistry().WithParents().WithFullPropertyTracking(),
            "registry-after-tracking" => context.WithFullPropertyTracking().WithRegistry().WithParents(),
            "registry-after-parents" => context.WithFullPropertyTracking().WithParents().WithRegistry(),
            _ => throw new ArgumentOutOfRangeException(nameof(registrationOrder))
        };

        var initializer = new InheritedFlagInitializer();
        return (context.WithService(() => initializer), initializer);
    }

    /// <summary>
    /// Builds root -> top -> middle -> child with the subtree detached, and disables the flag on
    /// <c>top</c>. That is the ancestor a registry resolved behind the descent would be missing when
    /// the child's getter first runs: the child's own registration pulls in its immediate parent on
    /// demand and the root attached earlier, so the gap falls two levels up rather than truncating
    /// the chain.
    /// </summary>
    private static FlagNode BuildScenario(IInterceptorSubjectContext context, out FlagNode root, out FlagNode child)
    {
        root = new FlagNode(context) { Name = "root", Enabled = true };

        child = new FlagNode { Name = "child", Enabled = true };
        var middle = new FlagNode { Name = "middle", Enabled = true, Child = child };
        var top = new FlagNode { Name = "top", Enabled = false, Child = middle };

        return top;
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenSubtreeIsAttached_ThenADerivedGetterResolvesEveryAncestorOnItsFirstEvaluation(string registrationOrder)
    {
        // Arrange
        var (context, initializer) = CreateContext(registrationOrder);
        var top = BuildScenario(context, out var root, out var child);

        // Act
        root.Child = top;

        // Assert
        Assert.Equal(3, initializer.FirstResolvedAncestors[child]);
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenAnAncestorIsDisabled_ThenTheDerivedGetterSeesItOnItsFirstEvaluation(string registrationOrder)
    {
        // Arrange
        var (context, initializer) = CreateContext(registrationOrder);
        var top = BuildScenario(context, out var root, out var child);

        // Act
        root.Child = top;

        // Assert: an unresolvable ancestor is skipped by the getter, so a hole at "top" leaves this
        // true. The composed flag is the observable consequence of the ordering.
        Assert.False(initializer.FirstInheritedEnabled[child]);
    }

    [Theory]
    [MemberData(nameof(RegistrationOrders))]
    public void WhenAnAncestorFlagChangesAfterAttach_ThenTheDerivedAttributeRecalculates(string registrationOrder)
    {
        // Arrange
        var (context, _) = CreateContext(registrationOrder);
        var top = BuildScenario(context, out var root, out var child);
        root.Child = top;

        var changes = new List<SubjectPropertyChange>();
        context
            .GetPropertyChangeObservable(ImmediateScheduler.Instance)
            .Where(change =>
                ReferenceEquals(change.Property.Subject, child) &&
                change.Property.Name == $"{EnabledPropertyName}@{InheritedEnabledAttribute}")
            .Subscribe(changes.Add);

        // Act
        top.Enabled = true;

        // Assert: the getter read the ancestor's flag through the registry while attaching, so that
        // read was recorded as a dependency and this write cascades. An ancestor missed at attach
        // would never have been recorded and no recalculation would fire.
        Assert.Contains(changes, change => change.GetNewValue<bool>());
    }

    /// <summary>
    /// Adds two derived attributes to every <c>Enabled</c> property: one reporting how many
    /// ancestors resolve through the registry, one composing this subject's flag with theirs. Both
    /// record the value produced by their first evaluation, which is the attach-time observation.
    /// </summary>
    private sealed class InheritedFlagInitializer : ISubjectPropertyInitializer
    {
        public ConcurrentDictionary<IInterceptorSubject, int> FirstResolvedAncestors { get; } = new();

        public ConcurrentDictionary<IInterceptorSubject, bool> FirstInheritedEnabled { get; } = new();

        public void InitializeProperty(RegisteredSubjectProperty property)
        {
            if (property.IsAttribute || property.Name != EnabledPropertyName)
            {
                return;
            }

            property.AddDerivedAttribute(
                ResolvedAncestorsAttribute, typeof(int),
                subject =>
                {
                    var count = CountResolvableAncestors(subject, []);
                    FirstResolvedAncestors.TryAdd(subject, count);
                    return count;
                },
                null);

            property.AddDerivedAttribute(
                InheritedEnabledAttribute, typeof(bool),
                subject =>
                {
                    var enabled = ComputeInheritedEnabled(subject);
                    FirstInheritedEnabled.TryAdd(subject, enabled);
                    return enabled;
                },
                null);
        }

        private static int CountResolvableAncestors(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
        {
            if (!visited.Add(subject))
            {
                return 0;
            }

            var count = 0;
            foreach (var parent in subject.GetParents())
            {
                var parentSubject = parent.Property.Subject;
                if (parentSubject.TryGetRegisteredSubject() is not null)
                {
                    count++;
                }

                count += CountResolvableAncestors(parentSubject, visited);
            }

            return count;
        }

        private static bool ComputeInheritedEnabled(IInterceptorSubject subject)
        {
            return ReadEnabled(subject) is not false && AllAncestorsEnabled(subject, []);
        }

        /// <summary>
        /// Reads every resolvable ancestor's flag rather than short-circuiting, so each one is
        /// recorded as a dependency of this attribute.
        /// </summary>
        private static bool AllAncestorsEnabled(IInterceptorSubject subject, HashSet<IInterceptorSubject> visited)
        {
            if (!visited.Add(subject))
            {
                return true;
            }

            var result = true;
            foreach (var parent in subject.GetParents())
            {
                var parentSubject = parent.Property.Subject;
                if (parentSubject.TryGetRegisteredSubject() is not null && ReadEnabled(parentSubject) is false)
                {
                    result = false;
                }

                if (!AllAncestorsEnabled(parentSubject, visited))
                {
                    result = false;
                }
            }

            return result;
        }

        private static bool? ReadEnabled(IInterceptorSubject subject)
        {
            return subject.TryGetRegisteredSubject()?.TryGetProperty(EnabledPropertyName)?.GetValue() as bool?;
        }
    }
}

[InterceptorSubject]
public partial class FlagNode
{
    public partial string Name { get; set; }

    public partial bool Enabled { get; set; }

    public partial FlagNode? Child { get; set; }

    public FlagNode()
    {
        Name = string.Empty;
    }
}
