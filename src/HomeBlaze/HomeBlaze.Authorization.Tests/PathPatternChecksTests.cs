using HomeBlaze.Authorization.Internal;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class PathPatternChecksTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenASubjectWithoutParentsIsNotAttached_ThenItIsNotARootForTheWarning(bool isAttached)
    {
        // Arrange
        var context = TestContext.Create();
        var root = new TestRoot(context);
        var otherRoot = new TestRoot(context)
        {
            Children = new Dictionary<string, IInterceptorSubject> { ["Kitchen"] = new TagTestSubject() }
        };

        // An instance created outside the registry is never attached.
        var otherRegistered = isAttached ? otherRoot.TryGetRegisteredSubject()! : new RegisteredSubject(otherRoot);
        var knownSubjects = new Dictionary<IInterceptorSubject, RegisteredSubject>
        {
            [root] = root.TryGetRegisteredSubject()!,
            [otherRoot] = otherRegistered
        };

        var role = new AuthorizationRole
        {
            Name = "Custom",
            Deny = [new PermissionRule { Paths = ["/Kitchen"], Actions = [PermissionAction.Read] }]
        };
        List<RegisteredSubject>? roots = null;

        // Act
        var warning = PathPatternChecks.GetWarning(role, knownSubjects, new DefaultPathProvider('/'), ref roots);

        // Assert
        Assert.Equal(isAttached, warning is not null);
    }
}
