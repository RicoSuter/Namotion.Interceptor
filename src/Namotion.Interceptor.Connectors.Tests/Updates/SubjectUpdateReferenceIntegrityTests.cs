using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

public class SubjectUpdateReferenceIntegrityTests
{
    [Theory]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.Relationships))]
    public void WhenABatchReferencesASubjectThatLeftTheGraph_ThenOnlyItsReferenceIsLeftOut(string propertyName)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry()) { FirstName = "Root" };
        var removed = new Person { FirstName = "Ada" };
        object? empty = propertyName switch
        {
            nameof(Person.Children) => new List<Person>(),
            nameof(Person.Relationships) => new Dictionary<string, Person>(),
            _ => null
        };
        object? referencing = propertyName switch
        {
            nameof(Person.Children) => new List<Person> { removed },
            nameof(Person.Relationships) => new Dictionary<string, Person> { ["child"] = removed },
            _ => removed
        };
        var property = source.TryGetRegisteredProperty(propertyName)!;
        property.SetValue(empty);
        property.SetValue(referencing);
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, timestamp, null, empty, referencing),
            SubjectPropertyChange.Create<string?>(new PropertyReference(source, nameof(Person.FirstName)),
                ChangeOrigin.Local, timestamp, null, null, "Root")
        ];
        property.SetValue(empty); // the referenced subject leaves the graph while the batch still names it

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert: a reference is left out whole, a container keeps its entry without the member
        Assert.Null(removed.TryGetRegisteredSubject());
        var rootProperties = Assert.Single(update.Subjects).Value;
        Assert.Equal(propertyName != nameof(Person.Father), rootProperties.TryGetValue(propertyName, out var propertyUpdate));
        Assert.Empty(propertyUpdate?.Items ?? []);
        Assert.Empty(update.CompleteSubjectIds!);
        Assert.Equal("Root", rootProperties[nameof(Person.FirstName)].Value);
    }

    [Fact]
    public void WhenACompleteUpdateIsCreatedForAModelWithADetachedProjection_ThenTheSnapshotIsBuiltAndApplied()
    {
        // Arrange: connector handshakes send this snapshot, so creating it must not throw
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry())
        {
            FirstName = "Ada",
            Children = [new Person { FirstName = "Grace" }]
        };
        Person? projected = null;
        source.TryGetRegisteredSubject()!.AddDerivedProperty("Projection", typeof(Person), _ => projected);
        projected = new Person { FirstName = "Projected" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry());
        Person? mirrored = new() { FirstName = "Existing" };
        target.TryGetRegisteredSubject()!.AddProperty("Projection", typeof(Person),
            _ => mirrored, (_, value) => mirrored = (Person?)value);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(projected.TryGetRegisteredSubject());
        Assert.Equal("Ada", target.FirstName);
        Assert.Equal("Grace", Assert.Single(target.Children).FirstName);
        Assert.Equal("Existing", mirrored!.FirstName);
    }

    [Fact]
    public void WhenAComputedSubjectProjectionChanges_ThenThePartialUpdateOmitsIt()
    {
        // Arrange
        var mother = new Person { FirstName = "Mother", LastName = "Subtree" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root", Mother = mother };
        source.TryGetRegisteredSubject()!.AddDerivedProperty("Projection", typeof(Person), subject => ((Person)subject).Mother);
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, "Projection"),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, mother)
        ];

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);

        // Assert
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey("Projection"));
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(nameof(Person.LastName)));
    }

    [Fact]
    public void WhenAComputedSubjectProjectionReturnsASubjectOfTheGraph_ThenTheCompleteUpdateOmitsIt()
    {
        // Arrange
        var mother = new Person { FirstName = "Mother" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root", Mother = mother };
        source.TryGetRegisteredSubject()!.AddDerivedProperty("Projection", typeof(Person), subject => ((Person)subject).Mother);

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        Assert.NotNull(mother.TryGetRegisteredSubject());
        Assert.NotNull(update.Subjects[update.Root!][nameof(Person.Mother)].Id);
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey("Projection"));
    }

    [Fact]
    public void WhenAnUpdateNamesAComputedSubjectProjection_ThenItIsIgnoredAndTheApplyTerminates()
    {
        // Arrange: the projection returns a new registered subject on every read, so an applier walking into
        // it never ends. Creating stops long before that could exhaust the stack.
        var context = InterceptorSubjectContext.Create().WithRegistry();
        var createdSubjects = 0;
        MintingProjectionNode Mint() => ++createdSubjects > 20
            ? throw new InvalidOperationException("The apply walked into the projection without bound.")
            : new MintingProjectionNode(context) { CreateProjected = Mint };
        var target = new MintingProjectionNode(context) { CreateProjected = Mint };
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [nameof(MintingProjectionNode.Name)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Applied" },
                    [nameof(MintingProjectionNode.Projection)] = new() { Kind = SubjectPropertyUpdateKind.Object, Id = "1" }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Equal("Applied", target.Name);
        Assert.Equal(0, createdSubjects);
    }

    [Fact]
    public void WhenASubjectWithoutRegistryMetadataHasAComputedProjection_ThenTheCompleteUpdateLeavesItOut()
    {
        // Arrange: without a registry every subject is serialized from its own metadata, and the projection
        // returns a new subject on every read
        var createdSubjects = 0;
        MintingProjectionNode Mint() => ++createdSubjects > 20
            ? throw new InvalidOperationException("The update walked into the projection without bound.")
            : new MintingProjectionNode { CreateProjected = Mint };
        var source = new MintingProjectionNode(InterceptorSubjectContext.Create()) { Name = "Root", CreateProjected = Mint };

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        var rootProperties = Assert.Single(update.Subjects).Value;
        Assert.Equal("Root", rootProperties[nameof(MintingProjectionNode.Name)].Value);
        Assert.False(rootProperties.ContainsKey(nameof(MintingProjectionNode.Projection)));
        Assert.Equal(0, createdSubjects);
    }

    [Fact]
    public void WhenTwoChangesReferenceASubjectThatLeftTheGraph_ThenBothReferencesAreLeftOutWithoutAWarning()
    {
        // Arrange
        var logger = new RecordingLogger();
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        context.AddService<ILoggerFactory>(new RecordingLoggerFactory(logger));
        var mother = new PersonWithRoot { FirstName = "Mother" };
        var person = new PersonWithRoot { FirstName = "Person", Mother = mother };
        var root = new PersonRoot(context) { Name = "Root", Person = person };
        var removed = new PersonWithRoot { FirstName = "Removed" };
        person.Father = removed;
        mother.Father = removed;
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<PersonWithRoot?>(new PropertyReference(person, nameof(PersonWithRoot.Father)),
                ChangeOrigin.Local, timestamp, null, null, removed),
            SubjectPropertyChange.Create<PersonWithRoot?>(new PropertyReference(mother, nameof(PersonWithRoot.Father)),
                ChangeOrigin.Local, timestamp, null, null, removed)
        ];
        person.Father = null; // the referenced subject leaves the graph while the batch still names it
        mother.Father = null;

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(root, changes, []);

        // Assert
        Assert.Empty(update.Subjects);
        Assert.Empty(update.CompleteSubjectIds!);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void WhenAReferencedSubjectLeftTheGraphBeforeTheBatchIsBuilt_ThenNoPropertyAProcessorExcludesIsSent()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "Root" };
        var added = new Person { FirstName = "Added", LastName = "Secret" };
        var batch = CaptureChanges(context, () => source.Father = added);
        source.Father = null;

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, batch, [new LastNameExcludingProcessor()]);

        // Assert
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(nameof(Person.LastName)));
        Assert.Empty(update.CompleteSubjectIds!);
    }

    [Fact]
    public void WhenAMemberLeavesTheGraphBeforeTheBatchAddingItIsBuilt_ThenTheMirrorConvergesWithTheSource()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var kept = new Person { FirstName = "Kept" };
        var source = new Person(context) { FirstName = "Root", Children = [kept] };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        var added = new Person { FirstName = "Added" };
        var leaving = new Person { FirstName = "Leaving" };
        var firstBatch = CaptureChanges(context, () => source.Children = [kept, added, leaving]);
        var secondBatch = CaptureChanges(context, () => source.Children = [kept, added]);

        // Act: the first batch is built only after the second batch's write took a member it adds out of the graph
        foreach (var batch in new[] { firstBatch, secondBatch })
        {
            mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, batch, []),
                DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        }

        // Assert
        Assert.Equal(["Kept", "Added"], mirror.Children.Select(child => child.FirstName));
    }

    [Fact]
    public void WhenACompleteUpdateIsBuiltWhileAnAddedMemberIsStillAttaching_ThenTheMirrorConvergesWithTheSource()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var kept = new Person { FirstName = "Kept" };
        var source = new Person(context) { FirstName = "Root", Children = [kept] };
        var added = new Person { FirstName = "Added" };

        // Runs innermost, so the snapshot a new client receives is built after the backing store holds the new
        // member and before the lifecycle attaches it.
        SubjectUpdate? snapshot = null;
        var isAddedRegisteredInSnapshot = true;
        context.AddService<IWriteInterceptor>(new AfterStoreWriteInterceptor(nameof(Person.Children), () =>
        {
            isAddedRegisteredInSnapshot = added.TryGetRegisteredSubject() is not null;
            snapshot = SubjectUpdate.CreateCompleteUpdate(source, []);
        }));
        var changes = CaptureChanges(context, () => source.Children = [kept, added]);
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());

        // Act
        mirror.ApplySubjectUpdate(snapshot!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        mirror.ApplySubjectUpdate(SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []),
            DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.False(isAddedRegisteredInSnapshot);
        Assert.Equal(["Kept", "Added"], mirror.Children.Select(child => child.FirstName));
    }

    [Theory]
    [InlineData(nameof(Person.Father))]
    [InlineData(nameof(Person.Children))]
    [InlineData(nameof(Person.Relationships))]
    public void WhenAPartialUpdateIsBuiltWhileAnAddedMemberIsStillAttaching_ThenTheMemberArrivesComplete(string propertyName)
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var source = new Person(context) { FirstName = "Root" };
        var mirror = new Person(InterceptorSubjectContext.Create().WithRegistry());
        mirror.ApplySubjectUpdate(SubjectUpdate.CreateCompleteUpdate(source, []), DefaultSubjectFactory.Instance, ChangeOrigin.Local);
        var added = new Person { FirstName = "Added" };
        object? oldValue = propertyName switch
        {
            nameof(Person.Children) => source.Children,
            nameof(Person.Relationships) => source.Relationships,
            _ => null
        };
        object newValue = propertyName switch
        {
            nameof(Person.Children) => new List<Person> { added },
            nameof(Person.Relationships) => new Dictionary<string, Person> { ["added"] = added },
            _ => added
        };

        // Runs innermost, so the update is built after the backing store holds the new member and before the
        // lifecycle attaches it.
        SubjectUpdate? update = null;
        var isAddedRegistered = true;
        context.AddService<IWriteInterceptor>(new AfterStoreWriteInterceptor(propertyName, () =>
        {
            isAddedRegistered = added.TryGetRegisteredSubject() is not null;
            SubjectPropertyChange[] changes =
            [
                SubjectPropertyChange.Create<object?>(new PropertyReference(source, propertyName),
                    ChangeOrigin.Local, DateTimeOffset.UtcNow, null, oldValue, newValue)
            ];
            update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        }));

        // Act
        source.TryGetRegisteredProperty(propertyName)!.SetValue(newValue);
        mirror.ApplySubjectUpdate(update!, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.False(isAddedRegistered);
        var mirrored = propertyName switch
        {
            nameof(Person.Children) => Assert.Single(mirror.Children),
            nameof(Person.Relationships) => Assert.Single(mirror.Relationships!).Value,
            _ => mirror.Father
        };
        Assert.Equal("Added", mirrored!.FirstName);
    }

    private static SubjectPropertyChange[] CaptureChanges(IInterceptorSubjectContext context, Action change)
    {
        var changes = new List<SubjectPropertyChange>();
        using (context.GetPropertyChangeObservable(System.Reactive.Concurrency.ImmediateScheduler.Instance).Subscribe(changes.Add))
        {
            change();
        }

        return changes.ToArray();
    }

    private sealed class LastNameExcludingProcessor : ISubjectUpdateProcessor
    {
        public bool IsIncluded(Namotion.Interceptor.Registry.Abstractions.RegisteredSubjectProperty property)
            => property.Name != nameof(Person.LastName);
    }

    /// <summary>Invokes a callback right after the backing store of one property was written.</summary>
    [RunsLast]
    private sealed class AfterStoreWriteInterceptor(string propertyName, Action afterWrite) : IWriteInterceptor
    {
        public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
        {
            next(ref context);
            if (context.Property.Name == propertyName)
            {
                afterWrite();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAComputedSubjectProjectionIsAnAttribute_ThenNoUpdateCarriesIt(bool partial)
    {
        // Arrange
        var projected = new Person { FirstName = "Projected" };
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        var registeredProperty = source.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!;
        registeredProperty.AddDerivedAttribute("Projection", typeof(Person), _ => projected, setValue: null);
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<string?>(new PropertyReference(source, nameof(Person.FirstName)),
                ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, "Root")
        ];

        // Act
        var update = partial
            ? SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, [])
            : SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.Values
            .Any(property => property.Attributes?.ContainsKey("Projection") == true));
        Assert.Equal(update.Root, Assert.Single(update.Subjects).Key);
    }

    [Fact]
    public void WhenASubjectHoldingAttributeReferencesAnUnregisteredSubject_ThenTheCompleteUpdateSerializesItFromItsMetadata()
    {
        // Arrange: the list is filled in place, so its subject is never attached and has no Registry metadata.
        var source = new Person(InterceptorSubjectContext.Create().WithRegistry()) { FirstName = "Root" };
        List<Person> friends = [];
        source.TryGetRegisteredSubject()!.TryGetProperty(nameof(Person.FirstName))!
            .AddAttribute("Friends", typeof(List<Person>), _ => friends, (_, value) => friends = (List<Person>)value!);
        friends.Add(new Person { FirstName = "Detached" });

        // Act
        var update = SubjectUpdate.CreateCompleteUpdate(source, []);

        // Assert
        var firstName = update.Subjects[update.Root!][nameof(Person.FirstName)];
        Assert.Equal("Root", firstName.Value);
        var friendId = Assert.Single(firstName.Attributes!["Friends"].Items!).Id;
        Assert.Equal("Detached", update.Subjects[friendId][nameof(Person.FirstName)].Value);
    }

    [Theory]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.Children))]
    public void WhenABatchUpdatesAChildAndThenClearsItsReference_ThenTheClearedReferenceCarriesNoStaleId(string childPropertyName)
    {
        // Arrange: a change below the referenced subject precedes the clear of the reference in the same batch.
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var mother = new Person { FirstName = "Ada" };
        source.Mother = mother;
        var oldChildren = mother.Children;
        mother.Children = [new Person { FirstName = "Kid" }];
        var timestamp = DateTimeOffset.UtcNow;
        SubjectPropertyChange[] changes =
        [
            childPropertyName == nameof(Person.Children)
                ? SubjectPropertyChange.Create<List<Person>>(new PropertyReference(mother, nameof(Person.Children)),
                    ChangeOrigin.Local, timestamp, null, oldChildren, mother.Children)
                : SubjectPropertyChange.Create<string?>(new PropertyReference(mother, nameof(Person.FirstName)),
                    ChangeOrigin.Local, timestamp, null, "Ada", "Grace"),
            SubjectPropertyChange.Create<Person?>(new PropertyReference(source, nameof(Person.Mother)),
                ChangeOrigin.Local, timestamp, null, mother, null)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Mother = new Person { FirstName = "Existing" }
        };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(update.Subjects[update.Root!][nameof(Person.Mother)].Id);
        Assert.Null(target.Mother);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnEarlierReferenceIsReplacedBeforeBatchCreation_ThenOnlyTheFinalReferenceIsRequired(bool clear)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var first = new Person { FirstName = "First" };
        var final = clear ? null : new Person { FirstName = "Final" };
        source.Father = first;
        source.Father = final;
        var property = new PropertyReference(source, nameof(Person.Father));
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<Person?>(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, null, first),
            SubjectPropertyChange.Create<Person?>(property, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, first, final)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Father = new Person() };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(first.TryGetRegisteredSubject());
        if (clear)
        {
            Assert.Null(update.Subjects[update.Root!][nameof(Person.Father)].Id);
            Assert.Null(target.Father);
        }
        else
        {
            Assert.Equal("Final", target.Father!.FirstName);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenATransientItemIsInsertedThenRemovedBeforeBatchCreation_ThenTheEmptyResultCarriesNoTransientPayload(bool dictionary)
    {
        // Arrange
        var source = new Person(InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry());
        var transient = new Person { FirstName = "Transient" };
        object empty = dictionary ? new Dictionary<string, Person>() : new List<Person>();
        object inserted = dictionary
            ? new Dictionary<string, Person> { ["child"] = transient }
            : new List<Person> { transient };
        var property = source.TryGetRegisteredProperty(dictionary ? nameof(Person.Relationships) : nameof(Person.Children))!;
        property.SetValue(inserted);
        property.SetValue(empty);
        SubjectPropertyChange[] changes =
        [
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, empty, inserted),
            SubjectPropertyChange.Create<object?>(property.Reference, ChangeOrigin.Local, DateTimeOffset.UtcNow, null, inserted, empty)
        ];
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new() };

        // Act
        var update = SubjectUpdate.CreatePartialUpdateFromChanges(source, changes, []);
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Null(transient.TryGetRegisteredSubject());
        Assert.Equal(update.Root, Assert.Single(update.Subjects).Key);
        var propertyUpdate = update.Subjects[update.Root!][property.Name];
        Assert.Equal(dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection, propertyUpdate.Kind);
        Assert.Empty(propertyUpdate.Items!);
        Assert.Empty(update.CompleteSubjectIds!);
        Assert.Empty(target.Children);
        Assert.Empty(target.Relationships!);
    }
}

/// <summary>
/// Model whose derived subject property creates a new subject on every read: a computed projection, which
/// no producer publishes and no applier may walk into.
/// </summary>
[InterceptorSubject]
public partial class MintingProjectionNode
{
    public Func<MintingProjectionNode>? CreateProjected { get; init; }

    public partial string? Name { get; set; }

    [Derived]
    public MintingProjectionNode? Projection => CreateProjected?.Invoke();
}
