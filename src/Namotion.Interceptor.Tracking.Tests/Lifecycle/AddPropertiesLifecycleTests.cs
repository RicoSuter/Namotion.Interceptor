using System.Collections;
using System.Collections.Concurrent;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Lifecycle;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Lifecycle;

/// <summary>
/// Tests for the lifecycle-aware <see cref="IInterceptorSubject.AddProperties"/> admission: one
/// call publishes metadata, initial ownership edges and property callbacks atomically, and a
/// rejected batch publishes nothing.
/// </summary>
public class AddPropertiesLifecycleTests
{
    private static IInterceptorSubjectContext CreateContext()
    {
        return InterceptorSubjectContext
            .Create()
            .WithContextInheritance();
    }

    private static SubjectPropertyMetadata CreateScalarProperty(string name)
    {
        return new SubjectPropertyMetadata(
            name, typeof(string), [], _ => "value", null, isIntercepted: true, isDynamic: true);
    }

    private static SubjectPropertyMetadata CreateStructuralProperty(
        string name, Func<IInterceptorSubject, object?> getValue, params Attribute[] attributes)
    {
        return new SubjectPropertyMetadata(
            name, typeof(Person), attributes, getValue, null, isIntercepted: true, isDynamic: true);
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnOwnedSubject_ThenTheInputSequenceIsEnumeratedExactlyOnce()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A"), CreateScalarProperty("B")]);

        // Act
        ((IInterceptorSubject)root).AddProperties(sequence);

        // Assert
        Assert.Equal(1, sequence.EnumerationCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("A"));
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("B"));
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnUnattachedSubject_ThenTheInputSequenceIsEnumeratedExactlyOnce()
    {
        // Arrange
        var root = new Person { FirstName = "R" };
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A")]);

        // Act
        ((IInterceptorSubject)root).AddProperties(sequence);

        // Assert
        Assert.Equal(1, sequence.EnumerationCount);
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("A"));
    }

    [Fact]
    public void WhenABatchNameCollidesWithAnExistingProperty_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var propertyCountBefore = subject.Properties.Count;
        var getterCalls = 0;
        var child = new Person { FirstName = "C" };
        var batch = new[]
        {
            CreateStructuralProperty("New", _ => { getterCalls++; return child; }),
            CreateScalarProperty("FirstName")
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The whole batch is rejected before classification: no metadata, no getter call, no edge.
        Assert.Equal(propertyCountBefore, subject.Properties.Count);
        Assert.False(subject.Properties.ContainsKey("New"));
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());
    }

    [Fact]
    public void WhenABatchContainsTheSameNameTwice_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;

        // Act & Assert
        Assert.Throws<InvalidOperationException>(
            () => subject.AddProperties(CreateScalarProperty("X"), CreateScalarProperty("X")));

        // Assert
        Assert.False(subject.Properties.ContainsKey("X"));
    }

    [Fact]
    public void WhenAStructuralPropertyIsAdmitted_ThenItsGetterIsInvokedExactlyOnceAndTheChildAttaches()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;

        // Act
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }));

        // Assert
        Assert.Equal(1, getterCalls);
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenABatchMixesScalarAndStructuralProperties_ThenAllPublishTogetherAndHandlersRunInInputOrder()
    {
        // Arrange
        var recorder = new RecordingPropertyHandler();
        var context = CreateContext().WithService(() => recorder, _ => false);
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        recorder.Target = subject;
        var child = new Person { FirstName = "C" };

        // Act
        subject.AddProperties(
            CreateScalarProperty("Scalar1"),
            CreateStructuralProperty("Struct1", _ => child),
            CreateScalarProperty("Scalar2"));

        // Assert
        Assert.True(subject.Properties.ContainsKey("Scalar1"));
        Assert.True(subject.Properties.ContainsKey("Struct1"));
        Assert.True(subject.Properties.ContainsKey("Scalar2"));
        Assert.Equal(1, child.GetReferenceCount());
        Assert.Equal(["Scalar1", "Struct1", "Scalar2"], recorder.AttachedProperties);
    }

    [Fact]
    public void WhenACapturedValueContainsAForeignSubject_ThenNothingIsPublished()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var foreign = new Person(foreignContext) { FirstName = "F" };
        var innocent = new Person { FirstName = "I" };
        var batch = new[]
        {
            CreateStructuralProperty("Innocent", _ => innocent),
            CreateStructuralProperty("Foreign", _ => foreign)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The batch fails as one unit: neither property publishes and the innocent subject stays
        // unattached even though its own subgraph was valid.
        Assert.False(subject.Properties.ContainsKey("Innocent"));
        Assert.False(subject.Properties.ContainsKey("Foreign"));
        Assert.Null(innocent.TryGetContext());
        Assert.Same(foreignContext, foreign.TryGetContext());
    }

    [Fact]
    public void WhenACompetingContextClaimsADiscoveredSubject_ThenProvisionalClaimsAreReleased()
    {
        // Arrange: the trap subject simulates the competing claim deterministically. Its structural
        // getter runs while the admission walks the prospective component, which is exactly the
        // window between validation and claiming, and attaches the trap to another context there.
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var innocent = new Person { FirstName = "I" };
        var trap = new ClaimTrapSubject();
        trap.ChildGetter = _ =>
        {
            trap.ChildGetter = null;
            trap.AttachToContext(foreignContext);
            return null;
        };

        var batch = new[]
        {
            new SubjectPropertyMetadata(
                "Trapped", typeof(IInterceptorSubject[]), [],
                _ => new IInterceptorSubject[] { innocent, trap },
                null, isIntercepted: true, isDynamic: true)
        };

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.AddProperties(batch));

        // The innocent subject was provisionally claimed before the trap lost the race, so the
        // failed batch must hand its claim back; the trap keeps its competing attachment.
        Assert.False(subject.Properties.ContainsKey("Trapped"));
        Assert.Null(innocent.TryGetContext());
        Assert.Same(foreignContext, ((IInterceptorSubject)trap).TryGetContext());
    }

    [Fact]
    public void WhenAHandlerAddsPropertiesDuringItsOwnAttachCallback_ThenTheBatchIsAdmitted()
    {
        // Arrange
        var child = new Person { FirstName = "C" };
        var added = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach && !added)
                {
                    added = true;
                    change.Subject.AddProperties(CreateStructuralProperty("Extra", _ => child));
                }
            }), _ => false);

        // Act
        var root = new Person(context) { FirstName = "R" };

        // Assert
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Extra"));
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenACallbackAddsPropertiesToAnUnattachedSubject_ThenOnlyMetadataIsPublished()
    {
        // Arrange
        var unattached = new Person { FirstName = "U" };
        var getterCalls = 0;
        var added = false;
        var context = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach && !added)
                {
                    added = true;
                    ((IInterceptorSubject)unattached).AddProperties(
                        CreateStructuralProperty("Extra", _ => { getterCalls++; return new Person(); }));
                }
            }), _ => false);

        // Act
        _ = new Person(context) { FirstName = "R" };

        // Assert: metadata only, no ownership work and no getter classification.
        Assert.True(((IInterceptorSubject)unattached).Properties.ContainsKey("Extra"));
        Assert.Null(unattached.TryGetContext());
        Assert.Equal(0, getterCalls);
    }

    [Fact]
    public void WhenACallbackAddsPropertiesToASubjectOfAnotherContext_ThenTheCallIsRejectedBeforeEnumeration()
    {
        // Arrange
        var contextB = CreateContext();
        var other = new Person(contextB) { FirstName = "B" };
        var otherPropertyCount = ((IInterceptorSubject)other).Properties.Count;
        var sequence = new CountingMetadataSequence([CreateScalarProperty("A")]);
        var contextA = CreateContext()
            .WithService(() => new DelegateLifecycleHandler(change =>
            {
                if (change.IsContextAttach)
                {
                    ((IInterceptorSubject)other).AddProperties(sequence);
                }
            }), _ => false);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => new Person(contextA));

        // Assert: rejected before the input was enumerated and before anything published.
        Assert.Equal(0, sequence.EnumerationCount);
        Assert.Equal(otherPropertyCount, ((IInterceptorSubject)other).Properties.Count);
    }

    [Fact]
    public void WhenADerivedStructuralPropertyIsAdmitted_ThenNoEdgeIsEstablishedAndTheGetterIsNotInvoked()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;

        // Act
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }, new DerivedAttribute()));

        // Assert: the metadata publishes, but a derived property never establishes ownership.
        Assert.True(((IInterceptorSubject)root).Properties.ContainsKey("Extra"));
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());
        Assert.Equal(0, child.GetReferenceCount());
    }

    [Fact]
    public void WhenPropertiesAreAddedToAnUnattachedSubject_ThenALaterAttachDiscoversThem()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person { FirstName = "R" };
        var child = new Person { FirstName = "C" };
        var getterCalls = 0;
        ((IInterceptorSubject)root).AddProperties(
            CreateStructuralProperty("Extra", _ => { getterCalls++; return child; }));

        // The unattached publication performs no ownership work.
        Assert.Equal(0, getterCalls);
        Assert.Null(child.TryGetContext());

        // Act
        root.AttachToContext(context);

        // Assert: the attach discovers the then-current structural property through its getter.
        Assert.Same(context, child.TryGetContext());
        Assert.Equal(1, child.GetReferenceCount());
    }

    [Fact]
    public void WhenABatchIsAdmitted_ThenThePublisherIsInvokedExactlyOnce()
    {
        // Arrange
        var context = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var publisherCalls = 0;
        var registration = new SubjectPropertyRegistrationContext(
            subject, [CreateScalarProperty("A")], _ => publisherCalls++);

        // Act
        subject.Executor.AddProperties(registration);

        // Assert
        Assert.Equal(1, publisherCalls);
    }

    [Fact]
    public void WhenABatchIsRejected_ThenThePublisherIsNeverInvoked()
    {
        // Arrange
        var context = CreateContext();
        var foreignContext = CreateContext();
        var root = new Person(context) { FirstName = "R" };
        var subject = (IInterceptorSubject)root;
        var foreign = new Person(foreignContext) { FirstName = "F" };
        var publisherCalls = 0;

        var duplicateRegistration = new SubjectPropertyRegistrationContext(
            subject, [CreateScalarProperty("FirstName")], _ => publisherCalls++);
        var foreignRegistration = new SubjectPropertyRegistrationContext(
            subject, [CreateStructuralProperty("Foreign", _ => foreign)], _ => publisherCalls++);

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() => subject.Executor.AddProperties(duplicateRegistration));
        Assert.Throws<InvalidOperationException>(() => subject.Executor.AddProperties(foreignRegistration));

        // Assert
        Assert.Equal(0, publisherCalls);
    }

    private sealed class CountingMetadataSequence(IReadOnlyList<SubjectPropertyMetadata> inner)
        : IEnumerable<SubjectPropertyMetadata>
    {
        public int EnumerationCount { get; private set; }

        public IEnumerator<SubjectPropertyMetadata> GetEnumerator()
        {
            EnumerationCount++;
            return inner.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class RecordingPropertyHandler : IPropertyLifecycleHandler
    {
        public IInterceptorSubject? Target { get; set; }

        public List<string> AttachedProperties { get; } = [];

        public void AttachProperty(SubjectPropertyLifecycleChange change)
        {
            if (ReferenceEquals(change.Subject, Target))
            {
                AttachedProperties.Add(change.Property.Name);
            }
        }

        public void DetachProperty(SubjectPropertyLifecycleChange change)
        {
        }
    }

    /// <summary>
    /// A minimal hand-written subject whose structural getter can run arbitrary code, used to
    /// interleave a competing context claim deterministically inside the admission walk.
    /// </summary>
    private sealed class ClaimTrapSubject : IInterceptorSubject
    {
        private IInterceptorExecutor? _context;
        private readonly Dictionary<string, SubjectPropertyMetadata> _properties;

        public Func<IInterceptorSubject, object?>? ChildGetter { get; set; }

        public ClaimTrapSubject()
        {
            _properties = new Dictionary<string, SubjectPropertyMetadata>
            {
                ["Child"] = new SubjectPropertyMetadata(
                    "Child", typeof(IInterceptorSubject), [],
                    subject => ((ClaimTrapSubject)subject).ChildGetter?.Invoke(subject),
                    null, isIntercepted: true, isDynamic: true)
            };
        }

        public object SyncRoot { get; } = new();

        public IInterceptorSubjectContext Context => InterceptorExecutor.GetOrCreate(ref _context, this);

        public IInterceptorExecutor Executor => InterceptorExecutor.GetOrCreate(ref _context, this);

        public ConcurrentDictionary<(string? property, string key), object?> Data { get; } = new();

        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties => _properties;

        public void AddProperties(params IEnumerable<SubjectPropertyMetadata> properties)
            => throw new NotSupportedException();
    }
}
