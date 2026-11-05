using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class SystemRulesTests
{
    [Fact]
    public void WhenAPersonHoldsEverything_ThenConfigurationWritesAndAdministrationExecutesAreDenied()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Person, BuiltInRoles.EverythingName);
        var subject = new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        Assert.False(policy.CanWrite(subject.TryGetProperty(nameof(TagTestSubject.Host))!));
        Assert.False(policy.IsAllowed(subject.TryGetProperty("Configure")!, AuthorizationActions.Execute));
        Assert.True(policy.IsAllowed(subject.TryGetProperty("Configure")!, AuthorizationActions.Administer));
        Assert.True(policy.IsAllowed(subject.TryGetProperty("Reset")!, AuthorizationActions.Execute));
    }

    [Fact]
    public void WhenAnAgentHoldsEverything_ThenAdministerIsDeniedEverywhere()
    {
        // Arrange
        var policy = Compile(PrincipalKind.NonHuman, BuiltInRoles.EverythingName);
        var subject = new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var member in subject.Properties)
        {
            Assert.False(policy.IsAllowed(member, AuthorizationActions.Administer), member.Name);
        }
    }

    [Fact]
    public void WhenAnonymousHoldsEverything_ThenAdministerIsDeniedEverywhere()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Anonymous, BuiltInRoles.EverythingName);
        var subject = new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        Assert.False(policy.IsAllowed(subject.TryGetProperty("Configure")!, AuthorizationActions.Administer));
    }

    [Fact]
    public void WhenAPersonHoldsEverything_ThenUserMembersCannotBeReadButAdministered()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Person, BuiltInRoles.EverythingName);
        var user = new User(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var member in user.Properties)
        {
            Assert.False(policy.CanRead(member), member.Name);
            Assert.False(policy.CanWrite(member), member.Name);
            Assert.True(policy.IsAllowed(member, AuthorizationActions.Administer), member.Name);
        }
    }

    [Fact]
    public void WhenAPersonHoldsOperator_ThenSecretsAreNeitherReadNorWrittenThroughThePolicy()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Person, BuiltInRoles.OperatorName);
        var subject = new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;
        var secret = subject.TryGetProperty(nameof(TagTestSubject.Password))!;

        // Act & Assert
        Assert.False(policy.CanRead(secret));
        Assert.False(policy.CanWrite(secret)); // secrets are configuration, which people change only as full administrators
    }

    [Fact]
    public void WhenAnonymousHoldsEverything_ThenUsersAreNotReadOrWrittenAndAuthorizationAndRolesNotWrittenOrExecuted()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Anonymous, BuiltInRoles.EverythingName);
        var context = TestContext.Create();
        var user = new User(context).TryGetRegisteredSubject()!;
        var authorization = new Authorization(context).TryGetRegisteredSubject()!;
        var role = new Role(context).TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var member in user.Properties)
        {
            Assert.False(policy.CanRead(member), member.Name);
            Assert.False(policy.CanWrite(member), member.Name);
        }

        AssertNotWrittenOrExecuted(policy, authorization);
        AssertNotWrittenOrExecuted(policy, role);
    }

    [Fact]
    public void WhenAPersonHoldsEverything_ThenAuthorizationAndRolesAreNotWrittenOrExecuted()
    {
        // Arrange
        var policy = Compile(PrincipalKind.Person, BuiltInRoles.EverythingName);
        var context = TestContext.Create();
        var authorization = new Authorization(context).TryGetRegisteredSubject()!;
        var role = new Role(context).TryGetRegisteredSubject()!;

        // Act & Assert
        AssertNotWrittenOrExecuted(policy, authorization);
        AssertNotWrittenOrExecuted(policy, role);
    }

    [Fact]
    public void WhenAnAgentHoldsEverything_ThenEveryActionOnUsersIsDenied()
    {
        // Arrange
        var policy = Compile(PrincipalKind.NonHuman, BuiltInRoles.EverythingName);
        var user = new User(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        foreach (var member in user.Properties)
        {
            foreach (var action in AuthorizationActions.All)
            {
                Assert.False(policy.IsAllowed(member, action), $"{member.Name} {action.Name}");
            }
        }
    }

    private static void AssertNotWrittenOrExecuted(AuthorizationPolicy policy, RegisteredSubject subject)
    {
        foreach (var member in subject.Properties)
        {
            Assert.False(policy.CanWrite(member), member.Name);
            Assert.False(policy.IsAllowed(member, AuthorizationActions.Execute), member.Name);
        }
    }

    private static AuthorizationPolicy Compile(PrincipalKind kind, string roleName) =>
        AuthorizationPolicy.Compile(
            BuiltInRoles.All, [roleName], SystemRules.For(kind), AuthorizationActions.All,
            new HomeBlazeMemberTagProvider(), new DefaultPathProvider('/'));
}
