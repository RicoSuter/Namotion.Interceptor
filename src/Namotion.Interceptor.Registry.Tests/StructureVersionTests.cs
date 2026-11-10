using System.Collections.Concurrent;
using System.Reflection;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Tests.Models;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class StructureVersionTests
{
    [Fact]
    public void WhenASubjectIsAttached_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.Father = new Person();

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenASubjectIsDetached_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context) { Father = new Person() };
        var before = registry.StructureVersion;

        // Act
        root.Father = null;

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenASubjectGainsASecondParent_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var child = new Person();
        var root = new Person(context) { Father = child };
        var before = registry.StructureVersion;

        // Act
        root.Mother = child;

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenASubjectLosesOneOfTwoParents_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var child = new Person();
        var root = new Person(context) { Father = child, Mother = child };
        var before = registry.StructureVersion;

        // Act
        root.Mother = null;

        // Assert
        Assert.NotNull(child.TryGetRegisteredSubject());
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenACollectionIsReordered_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var first = new Person();
        var second = new Person();
        var root = new Person(context) { Children = [first, second] };
        var before = registry.StructureVersion;

        // Act
        root.Children = [second, first];

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenADictionaryEntryIsReKeyed_ThenStructureVersionRisesToAnEvenValue()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["a"] = light } };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["b"] = light };

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenADictionaryGainsAnotherKeyForASubjectItHolds_ThenStructureVersionRisesAndChildrenHoldBothKeys()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["a"] = light } };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light };

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "a"), (light, "b"));
    }

    [Fact]
    public void WhenADictionaryLosesTheSecondOfTwoKeysForASubject_ThenStructureVersionRisesAndChildrenHoldTheRemainingKey()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light } };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light };

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "a"));
    }

    [Fact]
    public void WhenADictionaryReplacesTheSecondKeyOfASubject_ThenStructureVersionRisesAndChildrenHoldTheNewKey()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light } };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light, ["c"] = light };

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.LightsByName), (light, "a"), (light, "c"));
    }

    [Fact]
    public void WhenAListGainsAnotherPositionForASubjectItHolds_ThenStructureVersionRisesAndChildrenHoldBothPositions()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { Lights = [light] };
        var before = registry.StructureVersion;

        // Act
        group.Lights = [light, light];

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (light, 0), (light, 1));
    }

    [Fact]
    public void WhenAListLosesTheSecondOfTwoPositionsOfASubject_ThenStructureVersionRisesAndChildrenHoldTheRemainingPosition()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var group = new LightGroup(context) { Lights = [light, light] };
        var before = registry.StructureVersion;

        // Act
        group.Lights = [light];

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (light, 0));
    }

    [Fact]
    public void WhenAListHoldingSubjectsTwiceIsReorderedWithoutMovingTheirFirstPositions_ThenStructureVersionRisesAndChildrenFollowThePositions()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var first = new Light();
        var second = new Light();
        var group = new LightGroup(context) { Lights = [first, second, second, first] };
        var before = registry.StructureVersion;

        // Act
        group.Lights = [first, second, first, second];

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(group, nameof(LightGroup.Lights), (first, 0), (second, 1), (first, 2), (second, 3));
    }

    [Fact]
    public void WhenAnArrayGainsAnotherPositionForASubjectItHolds_ThenStructureVersionRisesAndChildrenHoldBothPositions()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var child = new Person();
        var root = new Person(context) { Children = [child] };
        var before = registry.StructureVersion;

        // Act
        root.Children = [child, child];

        // Assert
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
        AssertChildrenAndParents(root, nameof(Person.Children), (child, 0), (child, 1));
    }

    [Fact]
    public void WhenAListHoldingASubjectTwiceIsAssignedAgainWithTheSameItems_ThenStructureVersionIsUnchanged()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var first = new Light();
        var second = new Light();
        var group = new LightGroup(context) { Lights = [first, second, first] };
        var before = registry.StructureVersion;

        // Act
        group.Lights = [first, second, first];

        // Assert
        Assert.Equal(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenADictionaryHoldingASubjectTwiceIsRebuiltInAnotherOrderThatKeepsTheKeysOfEachSubjectInOrder_ThenStructureVersionIsUnchanged()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var light = new Light();
        var other = new Light();
        var group = new LightGroup(context)
        {
            LightsByName = new Dictionary<string, Light> { ["a"] = light, ["b"] = light, ["c"] = other }
        };
        var before = registry.StructureVersion;

        // Act
        group.LightsByName = new Dictionary<string, Light> { ["a"] = light, ["c"] = other, ["b"] = light };

        // Assert
        Assert.Equal(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenAPropertyIsAddedAtRunTime_ThenStructureVersionRisesByTwo()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.TryGetRegisteredSubject()!.AddProperty<string>("Dynamic", _ => "value");

        // Assert
        Assert.Equal(0, before % 2);
        Assert.Equal(before + 2, registry.StructureVersion);
    }

    [Fact]
    public void WhenAnAttributeIsAddedAtRunTime_ThenStructureVersionRisesByTwo()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.LastName))!.AddAttribute<string>("Hint", _ => "value");

        // Assert
        Assert.Equal(0, before % 2);
        Assert.Equal(before + 2, registry.StructureVersion);
    }

    [Fact]
    public void WhenAScalarPropertyIsWritten_ThenStructureVersionIsUnchanged()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        var before = registry.StructureVersion;

        // Act
        root.FirstName = "Ann";

        // Assert
        Assert.Equal(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenAChangeThrows_ThenStructureVersionIsEvenAgain()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var root = new Person(context);
        Person? value = null;
        ((IInterceptorSubject)root).AddProperties(new SubjectPropertyMetadata(
            "Unregistered", typeof(Person), [],
            _ => value,
            (_, newValue) => value = (Person?)newValue,
            isIntercepted: true, isDynamic: true));
        var before = registry.StructureVersion;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new PropertyReference(root, "Unregistered")
            .SetPropertyValueWithInterception(new Person(), null, (_, newValue) => value = (Person?)newValue));
        AssertRisenToAnEvenValue(before, registry.StructureVersion);
    }

    [Fact]
    public void WhenAChangeIsInProgress_ThenStructureVersionIsOdd()
    {
        // Arrange
        var (context, registry) = CreateContext();
        var subject = new ObservingSubject();
        var observed = new List<long>();
        subject.OnPropertiesRead = () => observed.Add(registry.StructureVersion);

        // Act
        ((IInterceptorSubject)subject).Context.AddFallbackContext(context);

        // Assert
        Assert.NotNull(registry.TryGetRegisteredSubject(subject));
        Assert.Contains(observed, version => version % 2 == 1);
        Assert.Equal(0, registry.StructureVersion % 2);
    }

    [Fact]
    public async Task WhenAChildIndexChangeIsInProgress_ThenStructureVersionIsOdd()
    {
        // Arrange: a reorder attaches and detaches nothing and runs no user code while the registry updates the
        // indices, so the test holds the lock the update takes next to stop the change while it is in progress.
        var (context, registry) = CreateContext();
        var first = new Person();
        var second = new Person();
        var root = new Person(context) { Children = [first, second] };
        var property = root.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.Children))!;
        var childrenLock = typeof(RegisteredSubjectProperty)
            .GetField("_children", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(property)!;
        var before = registry.StructureVersion;

        // Act
        long observedDuringChange;
        Task reorder;
        lock (childrenLock)
        {
            // A dedicated thread, because this test holds its thread-pool worker while it waits, and a work item queued
            // from there may not start within the timeout while concurrent tests hold every other worker.
            reorder = Task.Factory.StartNew(
                () => root.Children = [second, first],
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            SpinWait.SpinUntil(() => registry.StructureVersion % 2 == 1 || reorder.IsCompleted, TimeSpan.FromSeconds(30));
            observedDuringChange = registry.StructureVersion;
        }

        await reorder.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert
        Assert.Equal(before + 1, observedDuringChange);
        Assert.Equal(before + 2, registry.StructureVersion);
        Assert.Equal([second, first], property.Children.Select(child => child.Subject));
    }

    private static void AssertRisenToAnEvenValue(long before, long after)
    {
        Assert.Equal(0, before % 2);
        Assert.True(after > before);
        Assert.Equal(0, after % 2);
    }

    private static void AssertChildrenAndParents(
        IInterceptorSubject holder, string propertyName, params (IInterceptorSubject Subject, object Index)[] expected)
    {
        var property = holder.TryGetRegisteredSubject()!.TryGetProperty(propertyName)!;
        Assert.Equal(expected, property.Children.Select(child => (child.Subject, child.Index!)));

        foreach (var subject in expected.Select(entry => entry.Subject).Distinct())
        {
            Assert.Equal(
                expected.Where(entry => entry.Subject == subject).Select(entry => entry.Index),
                subject.TryGetRegisteredSubject()!.Parents.Where(parent => parent.Property == property).Select(parent => parent.Index!));
        }
    }

    private static (IInterceptorSubjectContext Context, ISubjectRegistry Registry) CreateContext()
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithRegistry();

        return (context, context.TryGetService<ISubjectRegistry>()!);
    }

    /// <summary>
    /// A hand-written subject, because the registry reads <see cref="IInterceptorSubject.Properties"/> while it registers
    /// a subject, which lets a test observe the registry from inside a change.
    /// </summary>
    private sealed class ObservingSubject : IInterceptorSubject
    {
        private static readonly IReadOnlyDictionary<string, SubjectPropertyMetadata> NoProperties =
            new Dictionary<string, SubjectPropertyMetadata>();

        private IInterceptorExecutor? _executor;

        public Action? OnPropertiesRead { get; set; }

        public object SyncRoot { get; } = new();

        IInterceptorSubjectContext IInterceptorSubject.Context => InterceptorExecutor.GetOrCreate(ref _executor, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties
        {
            get
            {
                OnPropertiesRead?.Invoke();
                return NoProperties;
            }
        }

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties) => throw new NotSupportedException();
    }
}
