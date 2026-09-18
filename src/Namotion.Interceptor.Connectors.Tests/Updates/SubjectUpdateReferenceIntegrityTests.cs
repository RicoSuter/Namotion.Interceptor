using Microsoft.Extensions.Logging;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
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
    public void WhenABatchReferencesASubjectThatLeftTheGraph_ThenCreationOmitsTheReferencingProperty(string propertyName)
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

        // Assert
        Assert.Null(removed.TryGetRegisteredSubject());
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(propertyName));
        Assert.Equal("Root", update.Subjects[update.Root][nameof(Person.FirstName)].Value);
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
        Assert.NotNull(update.Subjects[update.Root][nameof(Person.Mother)].Id);
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
    public void WhenNestedPropertiesAreOmitted_ThenTheWarningNamesEachOnceWithItsOwnerType()
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
        Assert.DoesNotContain(update.Subjects.Values, properties => properties.ContainsKey(nameof(PersonWithRoot.Father)));
        var warning = Assert.Single(logger.Warnings);
        var ownedPropertyName = $"{nameof(PersonWithRoot)}.{nameof(PersonWithRoot.Father)}";
        Assert.Equal(1, warning.Split(ownedPropertyName).Length - 1);
        Assert.DoesNotContain($"{nameof(PersonRoot)}.", warning);
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
    public void WhenASubjectHoldingAttributeReferencesAnUnregisteredSubject_ThenTheCompleteUpdateOmitsTheAttribute()
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
        var firstName = update.Subjects[update.Root][nameof(Person.FirstName)];
        Assert.Equal("Root", firstName.Value);
        Assert.False(firstName.Attributes?.ContainsKey("Friends") ?? false);
        Assert.Single(update.Subjects);
    }

    [Theory]
    [InlineData(nameof(Person.FirstName))]
    [InlineData(nameof(Person.Children))]
    public void WhenABatchUpdatesAChildAndThenClearsItsReference_ThenTheClearedReferenceCarriesNoStaleId(string childPropertyName)
    {
        // Arrange: a value change on the child runs after the clear, but a subject-holding one runs before it
        // and builds the path back to the root, which stamps the reference with the child's ID the clear reuses.
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
        Assert.Null(update.Subjects[update.Root][nameof(Person.Mother)].Id);
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
            Assert.Null(update.Subjects[update.Root][nameof(Person.Father)].Id);
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
        var propertyUpdate = update.Subjects[update.Root][property.Name];
        Assert.Equal(dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection, propertyUpdate.Kind);
        Assert.Equal(0, propertyUpdate.Count);
        Assert.Empty(propertyUpdate.Operations ?? []);
        Assert.Empty(propertyUpdate.Items ?? []);
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
