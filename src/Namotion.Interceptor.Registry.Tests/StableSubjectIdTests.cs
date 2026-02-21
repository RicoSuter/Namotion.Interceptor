using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Tracking;

namespace Namotion.Interceptor.Registry.Tests;

public class StableSubjectIdTests
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
    public void RegisterStableId_ThenTryGetSubjectByStableId_ReturnsSubject()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var person = new Models.Person(context) { FirstName = "Test" };
        var registry = context.GetService<ISubjectRegistry>();

        registry.RegisterStableId("testId123", (IInterceptorSubject)person);

        Assert.True(registry.TryGetSubjectByStableId("testId123", out var found));
        Assert.Same(person, found);
    }

    [Fact]
    public void TryGetSubjectByStableId_WithUnknownId_ReturnsFalse()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var registry = context.GetService<ISubjectRegistry>();

        Assert.False(registry.TryGetSubjectByStableId("unknown", out _));
    }

    [Fact]
    public void RegisterStableId_WhenSubjectDetached_RemovesFromReverseIndex()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking().WithRegistry();
        var child = new Models.Person { FirstName = "Child" };
        var parent = new Models.Person(context) { FirstName = "Parent", Mother = child };
        var registry = context.GetService<ISubjectRegistry>();

        registry.RegisterStableId("childId", (IInterceptorSubject)child);
        Assert.True(registry.TryGetSubjectByStableId("childId", out _));

        // Detach by setting to null
        parent.Mother = null;

        Assert.False(registry.TryGetSubjectByStableId("childId", out _));
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
        var registry = context.GetService<ISubjectRegistry>();

        var id = subject.GetOrAddSubjectId();

        Assert.True(registry.TryGetSubjectByStableId(id, out var found));
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
        var registry = context.GetService<ISubjectRegistry>();

        subject.SetSubjectId("assignedId");

        Assert.True(registry.TryGetSubjectByStableId("assignedId", out var found));
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
