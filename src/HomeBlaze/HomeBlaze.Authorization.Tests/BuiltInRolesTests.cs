using HomeBlaze.Abstractions;
using HomeBlaze.Authorization.Tests.Subjects;
using Namotion.Interceptor;
using Namotion.Interceptor.Authorization;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;
using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class BuiltInRolesTests
{
    public static TheoryData<string> RoleNames => [BuiltInRoles.ReaderName, BuiltInRoles.OperatorName, BuiltInRoles.EverythingName];

    [Theory]
    [MemberData(nameof(RoleNames))]
    public void WhenABuiltInRoleIsCompiledWithTheSystemRules_ThenItCompiles(string roleName)
    {
        // Act
        var policy = Compile(roleName);

        // Assert
        Assert.NotSame(AuthorizationPolicy.DenyAll, policy);
        Assert.Contains(policy.Roles, role => role.Name == roleName);
    }

    [Theory]
    [InlineData("Reader", true)]
    [InlineData("operator", true)]
    [InlineData("EVERYTHING", true)]
    [InlineData("MyRole", false)]
    public void WhenANameIsChecked_ThenBuiltInNamesMatchIgnoringCase(string name, bool expected)
    {
        // Act
        var isBuiltIn = BuiltInRoles.IsBuiltIn(name);

        // Assert
        Assert.Equal(expected, isBuiltIn);
    }

    [Fact]
    public void WhenTheRoleIsReader_ThenItReadsStateSubjectsAttributesMetadataAndMethods()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.ReaderName);
        var subject = CreateSubject();
        var subjectMetadata = TestMetadata.AddSubjectMetadata(subject);

        // Act & Assert
        Assert.True(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!));
        Assert.True(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Child))!));
        Assert.True(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!.TryGetAttribute(KnownAttributes.Minimum)!));
        Assert.True(policy.CanRead(subjectMetadata));
        Assert.True(policy.CanRead(subject.TryGetProperty("GetCount")!));
        Assert.True(policy.CanRead(subject.TryGetProperty("Start")!));
        Assert.False(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Host))!));
        Assert.False(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Plain))!));
    }

    [Fact]
    public void WhenTheRoleIsReader_ThenItReadsHistoryOfStateAndExecutesQueries()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.ReaderName);
        var subject = CreateSubject();

        // Act & Assert
        Assert.True(policy.IsAllowed(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!, AuthorizationActions.History));
        Assert.True(policy.IsAllowed(subject.TryGetProperty("GetCount")!, AuthorizationActions.Execute));
    }

    [Fact]
    public void WhenTheRoleIsReader_ThenItNeitherWritesStateNorExecutesOperations()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.ReaderName);
        var subject = CreateSubject();

        // Act & Assert
        Assert.False(policy.CanWrite(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!));
        Assert.False(policy.IsAllowed(subject.TryGetProperty("Start")!, AuthorizationActions.Execute));
        Assert.False(policy.IsAllowed(subject.TryGetProperty(nameof(TagTestSubject.Host))!, AuthorizationActions.History));
    }

    [Fact]
    public void WhenTheRoleIsOperator_ThenItExecutesOperationsAndWritesStateAndMetadata()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.OperatorName);
        var subject = CreateSubject();
        var subjectMetadata = TestMetadata.AddSubjectMetadata(subject);
        var propertyMetadata = TestMetadata.AddPropertyMetadata(subject, nameof(TagTestSubject.Temperature));

        // Act & Assert
        Assert.True(policy.IsAllowed(subject.TryGetProperty("Start")!, AuthorizationActions.Execute));
        Assert.True(policy.CanWrite(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!));
        Assert.True(policy.CanWrite(subjectMetadata));
        Assert.True(policy.CanWrite(propertyMetadata));
        Assert.True(policy.CanRead(subject.TryGetProperty(nameof(TagTestSubject.Temperature))!));
    }

    [Fact]
    public void WhenTheRoleIsOperator_ThenTheSystemRulesDenyConfigurationWritesAndConfirmationRequiredOperations()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.OperatorName);
        var subject = CreateSubject();

        // Act & Assert
        Assert.False(policy.CanWrite(subject.TryGetProperty(nameof(TagTestSubject.Host))!));
        Assert.False(policy.IsAllowed(subject.TryGetProperty("Reset")!, AuthorizationActions.Execute));
    }

    [Fact]
    public void WhenTheRoleIsEverything_ThenItAllowsEveryActionOnAPlainMember()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.EverythingName);
        var member = CreateSubject().TryGetProperty(nameof(TagTestSubject.Plain))!;

        // Act & Assert
        foreach (var action in AuthorizationActions.All)
        {
            Assert.True(policy.IsAllowed(member, action), action.Name);
        }
    }

    [Fact]
    public void WhenTheRoleIsEverything_ThenTheSystemRulesDenySecretsAndConfigurationWrites()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.EverythingName);
        var subject = CreateSubject();
        var secret = subject.TryGetProperty(nameof(TagTestSubject.Password))!;
        var configuration = subject.TryGetProperty(nameof(TagTestSubject.Host))!;

        // Act & Assert
        Assert.False(policy.CanRead(secret));
        Assert.False(policy.CanWrite(secret));
        Assert.False(policy.IsAllowed(secret, AuthorizationActions.History));
        Assert.True(policy.CanRead(configuration));
        Assert.False(policy.CanWrite(configuration));
    }

    [Fact]
    public void WhenTheRoleIsEverything_ThenTheSystemRulesDenyEverythingOnAnAgentDerivedFromTheAgentBase()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.EverythingName);
        var agent = new TestAgent(TestContext.Create()).TryGetRegisteredSubject()!;

        // Act & Assert
        AssertDeniesEveryAction(policy, agent);
    }

    [Fact]
    public void WhenTheRoleIsEverything_ThenTheSystemRulesDenyEverythingOnTheAuthorizationAndItsRoles()
    {
        // Arrange
        var policy = Compile(BuiltInRoles.EverythingName);
        var context = TestContext.Create();
        var role = new Role(context);
        var authorization = new Authorization(context)
        {
            Roles = new Dictionary<string, Role> { ["MyRole"] = role }
        };

        // Act & Assert
        AssertDeniesEveryAction(policy, authorization.TryGetRegisteredSubject()!);
        AssertDeniesEveryAction(policy, role.TryGetRegisteredSubject()!);
    }

    private static void AssertDeniesEveryAction(AuthorizationPolicy policy, RegisteredSubject subject)
    {
        Assert.NotEmpty(subject.Properties);
        foreach (var member in subject.Properties)
        {
            foreach (var action in AuthorizationActions.All)
            {
                Assert.False(policy.IsAllowed(member, action), $"{subject.Subject.GetType().Name}.{member.Name}: {action}");
            }
        }

        Assert.False(policy.CanRead(subject));
    }

    private static RegisteredSubject CreateSubject() =>
        new TagTestSubject(TestContext.Create()).TryGetRegisteredSubject()!;

    private static AuthorizationPolicy Compile(string roleName) =>
        AuthorizationPolicy.Compile(
            BuiltInRoles.All,
            [roleName],
            SystemRules.Rules,
            AuthorizationActions.All,
            new HomeBlazeMemberTagProvider(),
            new DefaultPathProvider('/'));
}
