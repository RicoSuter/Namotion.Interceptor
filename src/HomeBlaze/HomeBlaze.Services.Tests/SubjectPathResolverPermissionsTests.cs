using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Abstractions;
using HomeBlaze.Services.Tests.Models;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;

namespace HomeBlaze.Services.Tests;

/// <summary>
/// A path resolves for a viewer only through members the viewer may read.
/// </summary>
public class SubjectPathResolverPermissionsTests : SubjectPathResolverTestBase
{
    [Fact]
    public void WhenAReferenceOnACachedAbsolutePathIsNotReadable_ThenTheSubjectBehindItResolvesToNullForTheViewerOnly()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        RootManager.Root = new TestContainer(Context) { Name = "Root", Child = child };
        var unrestricted = Resolver.ResolveSubject("/Child/Child", PathStyle.Canonical);
        var permissions = new MemberPermissions(GetMember(child, nameof(TestContainer.Child)));

        // Act
        var forViewer = Resolver.ResolveSubject("/Child/Child", PathStyle.Canonical, relativeTo: null, permissions);
        var parentForViewer = Resolver.ResolveSubject("/Child", PathStyle.Canonical, relativeTo: null, permissions);
        var again = Resolver.ResolveSubject("/Child/Child", PathStyle.Canonical);

        // Assert
        Assert.Same(grandchild, unrestricted);
        Assert.Null(forViewer);
        Assert.Same(child, parentForViewer);
        Assert.Same(grandchild, again);
    }

    [Fact]
    public void WhenAReferenceOnARelativePathIsNotReadable_ThenTheSubjectBehindItResolvesToNull()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        var root = new TestContainer(Context) { Name = "Root", Child = child };
        RootManager.Root = root;
        var permissions = new MemberPermissions(GetMember(child, nameof(TestContainer.Child)));

        // Act
        var forViewer = Resolver.ResolveSubject("./Child/Child", PathStyle.Canonical, root, permissions);

        // Assert
        Assert.Null(forViewer);
    }

    [Fact]
    public void WhenThePathGoesUpThroughAMemberTheViewerMayNotRead_ThenItResolvesToNull()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        RootManager.Root = new TestContainer(Context) { Name = "Root", Child = child };
        var permissions = new MemberPermissions(GetMember(child, nameof(TestContainer.Child)));

        // Act
        var forViewer = Resolver.ResolveSubject("../", PathStyle.Canonical, grandchild, permissions);
        var unrestricted = Resolver.ResolveSubject("../", PathStyle.Canonical, grandchild);

        // Assert
        Assert.Null(forViewer);
        Assert.Same(child, unrestricted);
    }

    [Fact]
    public void WhenThePathGoesUpToASubjectTheViewerIsNotShown_ThenItResolvesToNull()
    {
        // Arrange
        var grandchild = new TestContainer(Context) { Name = "Grandchild" };
        var child = new TestContainer(Context) { Name = "Child", Child = grandchild };
        RootManager.Root = new TestContainer(Context) { Name = "Root", Child = child };
        var permissions = new MemberPermissions(hiddenSubject: child);

        // Act
        var forViewer = Resolver.ResolveSubject("../", PathStyle.Canonical, grandchild, permissions);
        var downAgain = Resolver.ResolveSubject("../Child", PathStyle.Canonical, grandchild, permissions);

        // Assert
        Assert.Null(forViewer);
        Assert.Null(downAgain);
    }

    private static RegisteredSubjectProperty GetMember(IInterceptorSubject subject, string name) =>
        subject.TryGetRegisteredSubject()!.TryGetProperty(name)!;

    // May read every member except one and see every subject except one.
    private sealed class MemberPermissions(RegisteredSubjectProperty? unreadable = null, IInterceptorSubject? hiddenSubject = null) : IUserPermissions
    {
        public event Action? Changed
        {
            add { }
            remove { }
        }

        public bool CanRead(RegisteredSubjectProperty member) => member != unreadable;

        public bool CanWrite(RegisteredSubjectProperty member) => false;

        public bool CanInvoke(RegisteredSubjectProperty member) => false;

        public bool CanAdminister(RegisteredSubjectProperty member) => false;

        public bool CanReadHistory(RegisteredSubjectProperty member) => false;

        public bool CanShow(RegisteredSubject subject) => !ReferenceEquals(subject.Subject, hiddenSubject);

        public bool HoldsAllRoles(IReadOnlyCollection<string> roles) => false;

        public bool CanActThrough(IReadOnlyCollection<string> roles) => false;

        public IReadOnlyList<string> GetRolesWithDenyRulesMissingFrom(IReadOnlyCollection<string> roles) => [];
    }
}
