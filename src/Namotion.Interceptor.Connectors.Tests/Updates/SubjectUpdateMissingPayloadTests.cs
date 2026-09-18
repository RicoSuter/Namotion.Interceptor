using Namotion.Interceptor.Connectors.Tests.Models;
using Namotion.Interceptor.Connectors.Updates;
using Namotion.Interceptor.Registry;

namespace Namotion.Interceptor.Connectors.Tests.Updates;

/// <summary>
/// Verifies that no subject is created for an ID whose payload the update does not carry, even when the
/// update marks every subject complete: only the property naming it fails.
/// </summary>
public class SubjectUpdateMissingPayloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAnItemReferencesAMissingSubject_ThenApplyReportsFailureAndPreservesTheContainer(bool dictionary)
    {
        // Arrange
        var child = new Person { FirstName = "Old" };
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry())
        {
            Children = [child],
            Relationships = new Dictionary<string, Person> { ["child"] = child }
        };
        var originalChildren = target.Children;
        var originalRelationships = target.Relationships;
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? nameof(Person.Relationships) : nameof(Person.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                        Items = [new SubjectPropertyItemUpdate { Id = "missing", Key = dictionary ? "child" : null }]
                    },
                    [nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Updated" }
                }
            }
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local));

        // Assert
        Assert.Contains("missing", exception.Message);
        Assert.Same(originalChildren, target.Children);
        Assert.Same(originalRelationships, target.Relationships);
        Assert.Same(child, Assert.Single(target.Children));
        Assert.Equal("Old", child.FirstName);
        Assert.Equal("Updated", target.FirstName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAMissingSubjectIsListedAfterACreatedOne_ThenALaterReferenceRootsTheCreatedSubject(bool dictionary)
    {
        // Arrange: the container fails after it created a subject, which a later reference to its ID then roots
        var target = new Person(InterceptorSubjectContext.Create().WithRegistry()) { Relationships = new() };
        var factory = new CountingSubjectFactory();
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? nameof(Person.Relationships) : nameof(Person.Children)] = new SubjectPropertyUpdate
                    {
                        Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                        Items =
                        [
                            new SubjectPropertyItemUpdate { Id = "created", Key = dictionary ? "created" : null },
                            new SubjectPropertyItemUpdate { Id = "missing", Key = dictionary ? "missing" : null }
                        ]
                    },
                    [nameof(Person.Father)] = new() { Kind = SubjectPropertyUpdateKind.Object, Id = "created" }
                },
                ["created"] = new() { [nameof(Person.FirstName)] = new() { Kind = SubjectPropertyUpdateKind.Value, Value = "Created" } }
            }
        };

        // Act
        Assert.Throws<InvalidOperationException>(() =>
            target.ApplySubjectUpdate(update, factory, ChangeOrigin.Local));

        // Assert
        Assert.Equal(1, factory.CreatedSubjects);
        Assert.Empty(target.Children);
        Assert.Empty(target.Relationships!);
        Assert.Equal("Created", target.Father!.FirstName);
        Assert.NotNull(target.Father.TryGetRegisteredSubject());
        Assert.True(target.TryGetRegisteredSubject()!.Subject.Context.GetService<Namotion.Interceptor.Registry.Abstractions.ISubjectIdRegistry>()
            .TryGetSubjectById("created", out var registered));
        Assert.Same(target.Father, registered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WhenAContainerWithoutASetterReferencesAMissingSubject_ThenItIsLeftAsItIs(bool dictionary)
    {
        // Arrange
        var target = new InitOnlyTypesTestNode(InterceptorSubjectContext.Create().WithRegistry());
        var update = new SubjectUpdate
        {
            Root = "1",
            Subjects = new()
            {
                ["1"] = new()
                {
                    [dictionary ? nameof(InitOnlyTypesTestNode.Lookup) : nameof(InitOnlyTypesTestNode.Items)] = new()
                    {
                        Kind = dictionary ? SubjectPropertyUpdateKind.Dictionary : SubjectPropertyUpdateKind.Collection,
                        Items = [new SubjectPropertyItemUpdate { Id = "missing", Key = dictionary ? "added" : null }]
                    }
                }
            }
        };

        // Act
        target.ApplySubjectUpdate(update, DefaultSubjectFactory.Instance, ChangeOrigin.Local);

        // Assert
        Assert.Empty(target.Items);
        Assert.Empty(target.Lookup);
    }

    private sealed class CountingSubjectFactory : ISubjectFactory
    {
        public int CreatedSubjects { get; private set; }

        public IInterceptorSubject CreateSubject(Type type, IServiceProvider? serviceProvider)
        {
            CreatedSubjects++;
            return DefaultSubjectFactory.Instance.CreateSubject(type, serviceProvider);
        }

        public IEnumerable<IInterceptorSubject?> CreateSubjectCollection(Type propertyType, params IEnumerable<IInterceptorSubject?> children)
            => DefaultSubjectFactory.Instance.CreateSubjectCollection(propertyType, children);

        public System.Collections.IDictionary CreateSubjectDictionary(Type propertyType, IDictionary<object, IInterceptorSubject> entries)
            => DefaultSubjectFactory.Instance.CreateSubjectDictionary(propertyType, entries);
    }
}
