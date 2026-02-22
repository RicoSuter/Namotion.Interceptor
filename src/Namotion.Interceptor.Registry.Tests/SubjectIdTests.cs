using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class SubjectIdTests
{
    [Fact]
    public void GenerateSubjectId_Returns22CharBase62String()
    {
        var id = SubjectRegistryExtensions.GenerateSubjectId();

        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), c =>
            Assert.True(char.IsLetterOrDigit(c), $"Character '{c}' is not alphanumeric"));
    }

    [Fact]
    public void GenerateSubjectId_ProducesUniqueIds()
    {
        var ids = Enumerable.Range(0, 1000)
            .Select(_ => SubjectRegistryExtensions.GenerateSubjectId())
            .ToHashSet();

        Assert.Equal(1000, ids.Count);
    }

    [Fact]
    public void SetSubjectId_ThenTryGetSubjectById_ReturnsSubject()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        subject.SetSubjectId("testId123");

        Assert.True(idRegistry.TryGetSubjectById("testId123", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void TryGetSubjectById_WithUnknownId_ReturnsFalse()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        Assert.False(idRegistry.TryGetSubjectById("unknown", out _));
    }

    [Fact]
    public void SetSubjectId_WhenSubjectDetached_RemovesFromReverseIndex()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        child.SetSubjectId("childId");
        Assert.True(idRegistry.TryGetSubjectById("childId", out _));

        // Detach by setting to null
        parent.Mother = null;

        Assert.False(idRegistry.TryGetSubjectById("childId", out _));
    }

    [Fact]
    public void GetOrAddSubjectId_ReturnsSameIdOnMultipleCalls()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        var id1 = subject.GetOrAddSubjectId();
        var id2 = subject.GetOrAddSubjectId();

        Assert.Equal(id1, id2);
        Assert.Equal(22, id1.Length);
    }

    [Fact]
    public void GetOrAddSubjectId_RegistersInReverseIndex()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        var id = subject.GetOrAddSubjectId();

        Assert.True(idRegistry.TryGetSubjectById(id, out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_AssignsKnownId()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        subject.SetSubjectId("myCustomId");

        var id = subject.GetOrAddSubjectId();
        Assert.Equal("myCustomId", id);
    }

    [Fact]
    public void SetSubjectId_RegistersInReverseIndex()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        subject.SetSubjectId("assignedId");

        Assert.True(idRegistry.TryGetSubjectById("assignedId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void GetOrAddSubjectId_WithoutRegistry_StillWorks()
    {
        // Context without registry — GetOrAddSubjectId should still generate an ID
        var context = InterceptorSubjectContext.Create();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;

        var id = subject.GetOrAddSubjectId();

        Assert.Equal(22, id.Length);
        Assert.All(id.ToCharArray(), c => Assert.True(char.IsLetterOrDigit(c)));
    }

    [Fact]
    public void SetSubjectId_ReplacingExistingId_UnregistersOldId()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        subject.SetSubjectId("oldId");
        Assert.True(idRegistry.TryGetSubjectById("oldId", out _));

        subject.SetSubjectId("newId");

        Assert.False(idRegistry.TryGetSubjectById("oldId", out _));
        Assert.True(idRegistry.TryGetSubjectById("newId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_WithSameId_DoesNotFail()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var subject = (IInterceptorSubject)person;
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        subject.SetSubjectId("sameId");
        subject.SetSubjectId("sameId");

        Assert.True(idRegistry.TryGetSubjectById("sameId", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void SetSubjectId_WithIdAlreadyUsedByDifferentSubject_Throws()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person1 = new Models.Person(context) { FirstName = "Person1" };
        var person2 = new Models.Person(context) { FirstName = "Person2" };

        person1.SetSubjectId("sharedId");

        Assert.Throws<InvalidOperationException>(() =>
            person2.SetSubjectId("sharedId"));
    }
}
