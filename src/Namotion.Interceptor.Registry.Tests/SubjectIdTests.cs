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
    public void RegisterSubjectId_ThenTryGetSubjectById_ReturnsSubject()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        idRegistry.RegisterSubjectId("testId123", (IInterceptorSubject)person);

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
    public void RegisterSubjectId_WhenSubjectDetached_RemovesFromReverseIndex()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var idRegistry = context.GetService<ISubjectIdRegistry>();

        ((IInterceptorSubject)child).SetSubjectId("childId");
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
}
