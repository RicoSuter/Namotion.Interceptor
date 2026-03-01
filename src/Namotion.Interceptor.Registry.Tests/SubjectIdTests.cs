using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectIdTests
{
    [Fact]
    public void GenerateSubjectId_Returns22CharBase62String()
    {
        // Act
        var id = SubjectRegistryExtensions.GenerateSubjectId();

        // Assert
        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), c =>
            Assert.True(char.IsLetterOrDigit(c), $"Character '{c}' is not alphanumeric"));
    }

    [Fact]
    public void GenerateSubjectId_ProducesUniqueIds()
    {
        // Act
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => SubjectRegistryExtensions.GenerateSubjectId())
            .ToHashSet();

        // Assert
        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public void SetSubjectId_ThenTryGetSubjectById_ReturnsSubject()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act
        subject.SetSubjectId("testId123");

        // Assert
        Assert.True(idRegistry.TryGetSubjectById("testId123", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void TryGetSubjectById_WithUnknownId_ReturnsFalse()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act & Assert
        Assert.False(idRegistry.TryGetSubjectById("unknown", out _));
    }

    [Fact]
    public void SetSubjectId_WhenSubjectDetached_RemovesFromReverseIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Act
        parent.Mother = null;

        // Assert
        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void GetOrAddSubjectId_ReturnsSameIdOnMultipleCalls()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        // Act
        var id1 = subject.GetOrAddSubjectId();
        var id2 = subject.GetOrAddSubjectId();

        // Assert
        Assert.Equal(id1, id2);
        Assert.Equal(22, id1.Length);
    }

    [Fact]
    public void GetOrAddSubjectId_RegistersInReverseIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act
        var id = subject.GetOrAddSubjectId();

        // Assert
        Assert.True(idRegistry.TryGetSubjectById(id, out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_AssignsKnownId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        // Act
        subject.SetSubjectId("myCustomId");

        // Assert
        var id = subject.GetOrAddSubjectId();
        Assert.Equal("myCustomId", id);
    }

    [Fact]
    public void SetSubjectId_RegistersInReverseIndex()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act
        subject.SetSubjectId("assignedId");

        // Assert
        Assert.True(idRegistry.TryGetSubjectById("assignedId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void GetOrAddSubjectId_WithoutRegistry_StillWorks()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        // Act
        var id = subject.GetOrAddSubjectId();

        // Assert
        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void SetSubjectId_ReplacingExistingId_UnregistersOldId()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        subject.SetSubjectId("oldId");
        Assert.True(idRegistry.TryGetSubjectById("oldId", out _));

        // Act
        subject.SetSubjectId("newId");

        // Assert
        Assert.False(idRegistry.TryGetSubjectById("oldId", out _));
        Assert.True(idRegistry.TryGetSubjectById("newId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_WithSameId_DoesNotFail()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act
        subject.SetSubjectId("sameId");
        subject.SetSubjectId("sameId");

        // Assert
        Assert.True(idRegistry.TryGetSubjectById("sameId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_WithIdAlreadyUsedByDifferentSubject_Throws()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person1 = new Models.Person(context) { FirstName = "Person1" };
        var person2 = new Models.Person(context) { FirstName = "Person2" };

        person1.SetSubjectId("sharedId");

        // Act & Assert
        Assert.Throws<InvalidOperationException>(() =>
            person2.SetSubjectId("sharedId"));
    }

    [Fact]
    public void SetSubjectId_BeforeAttach_AutoRegistersOnAttach()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        child.SetSubjectId("preAssignedId");
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act - attaching the child to a parent with registry triggers auto-registration
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };

        // Assert
        Assert.True(idRegistry.TryGetSubjectById("preAssignedId", out var found));
        Assert.Same(child, found);
    }

    [Fact]
    public void SetSubjectId_BeforeAttach_DuplicateIdOnAttach_SkipsRegistration()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var parent = new Models.Person(context) { FirstName = "Parent" };
        parent.SetSubjectId("duplicateId");

        var child = new Models.Person { FirstName = "Child" };
        child.SetSubjectId("duplicateId");
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        // Act - attaching a child with a conflicting ID skips reverse index
        // registration but does not throw, so the lifecycle completes normally
        parent.Mother = child;

        // Assert - parent keeps the reverse index entry, child is attached but not indexed
        Assert.Same(child, parent.Mother);
        Assert.True(idRegistry.TryGetSubjectById("duplicateId", out var found));
        Assert.Same(parent, found);
    }

    [Fact]
    public void SetSubjectId_WithNullOrWhitespace_Throws()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => subject.SetSubjectId(null!));
        Assert.Throws<ArgumentException>(() => subject.SetSubjectId(""));
        Assert.Throws<ArgumentException>(() => subject.SetSubjectId("   "));
    }

    [Fact]
    public void TryGetSubjectId_WithNoIdAssigned_ReturnsNull()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        // Act
        var id = subject.TryGetSubjectId();

        // Assert
        Assert.Null(id);
    }
}
