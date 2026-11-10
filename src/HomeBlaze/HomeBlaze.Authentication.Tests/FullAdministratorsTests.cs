using HomeBlaze.AI.Mcp;
using HomeBlaze.Authentication.Internal;
using HomeBlaze.Authorization;
using Namotion.Interceptor.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class FullAdministratorsTests
{
    private readonly AuthorizationService _service = new(TestContext.Create(), new RulePathProvider());

    [Fact]
    public void WhenTheOtherAdministratorAlsoHoldsAnUnknownRole_ThenLockingTheLastUsableOneIsRefused()
    {
        // Arrange
        List<AuthorizationRole> roles = [];
        PersonRoleSet[] current = [Person(BuiltInRoles.EverythingName), Person(BuiltInRoles.EverythingName, "ghost")];
        PersonRoleSet[] candidate = [Person(BuiltInRoles.EverythingName) with { IsLockedByAdministrator = true }, current[1]];

        // Act
        var error = FullAdministrators.GetRemovalError(_service, roles, roles, current, candidate);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenTheOtherAdministratorAlsoHoldsARoleThatDoesNotCompile_ThenLockingTheLastUsableOneIsRefused()
    {
        // Arrange
        List<AuthorizationRole> roles = [BadPatternRole("broken")];
        PersonRoleSet[] current = [Person(BuiltInRoles.EverythingName), Person(BuiltInRoles.EverythingName, "broken")];
        PersonRoleSet[] candidate = [Person(BuiltInRoles.EverythingName) with { IsLockedByAdministrator = true }, current[1]];

        // Act
        var error = FullAdministrators.GetRemovalError(_service, roles, roles, current, candidate);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenTheRoleOfTheOnlyAdministratorWouldNotCompile_ThenTheChangeIsRefused()
    {
        // Arrange
        List<AuthorizationRole> current = [new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.EverythingName] }];
        List<AuthorizationRole> candidate = [BadPatternRole("admins") with { Includes = [BuiltInRoles.EverythingName] }];
        PersonRoleSet[] people = [Person("admins")];

        // Act
        var error = FullAdministrators.GetRemovalError(_service, current, candidate, people, people);

        // Assert
        Assert.NotNull(error);
    }

    [Fact]
    public void WhenAnotherUsableAdministratorRemains_ThenLockingOneIsAllowed()
    {
        // Arrange
        List<AuthorizationRole> roles = [new AuthorizationRole { Name = "admins", Includes = [BuiltInRoles.EverythingName] }];
        PersonRoleSet[] current = [Person(BuiltInRoles.EverythingName), Person("admins")];
        PersonRoleSet[] candidate = [Person(BuiltInRoles.EverythingName) with { IsLockedByAdministrator = true }, current[1]];

        // Act
        var error = FullAdministrators.GetRemovalError(_service, roles, roles, current, candidate);

        // Assert
        Assert.Null(error);
    }

    [Fact]
    public void WhenNoUsableAdministratorExistsNow_ThenNothingIsRefused()
    {
        // Arrange
        List<AuthorizationRole> roles = [];
        PersonRoleSet[] current = [Person(BuiltInRoles.EverythingName, "ghost")];
        PersonRoleSet[] candidate = [Person(BuiltInRoles.ReaderName)];

        // Act
        var error = FullAdministrators.GetRemovalError(_service, roles, roles, current, candidate);

        // Assert
        Assert.Null(error);
    }

    private static PersonRoleSet Person(params string[] roleNames) => new(roleNames, IsLockedByAdministrator: false);

    // A path pattern without the leading separator, which does not compile.
    private static AuthorizationRole BadPatternRole(string name) => new()
    {
        Name = name,
        Allow = [new PermissionRule { Paths = ["Home/**"], Actions = [PermissionAction.Read] }]
    };
}
