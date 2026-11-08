using HomeBlaze.Authorization;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class UserDelegationTests
{
    private static readonly IReadOnlySet<string> ManagerClosure = Closure("usermanager", "family", "guest", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);

    // Kid includes Operator and denies writing below /Heating, so Kid is the role with deny rules of this manager.
    private static readonly IReadOnlySet<string> KidManagerClosure = Closure("usermanager", "kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);

    [Fact]
    public void WhenAManagerAssignsARoleTheyHold_ThenItIsAllowed()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(Actor(ManagerClosure), Target("kid", ["guest"]), UserAction.SetRoles, ["guest", "family"], Closure("guest", "family"));

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAManagerAssignsARoleTheyDoNotHold_ThenItIsRefused()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(Actor(ManagerClosure), Target("kid", []), UserAction.SetRoles, [BuiltInRoles.EverythingName], Closure(BuiltInRoles.EverythingName));

        // Assert
        Assert.Contains(BuiltInRoles.EverythingName, refusal);
    }

    [Fact]
    public void WhenAManagerHoldsARoleOnlyThroughAnInclude_ThenTheyMayAssignIt()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(Actor(ManagerClosure), Target("kid", []), UserAction.SetRoles, [BuiltInRoles.ReaderName], Closure(BuiltInRoles.ReaderName));

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAManagerChangesTheirOwnRoles_ThenItIsRefused()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(Actor(ManagerClosure, "me"), Target("me", ["usermanager"]), UserAction.SetRoles, ["usermanager", "family"], Closure("usermanager", "family"));

        // Assert
        Assert.NotNull(refusal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenAPersonResetsTheirOwnPassword_ThenItIsRefusedWithAPointerToTheAccountDialog(bool isFullAdministrator)
    {
        // Arrange
        var actor = Actor(isFullAdministrator ? Closure(BuiltInRoles.EverythingName) : ManagerClosure, "me");

        // Act
        var refusal = UserDelegation.GetRefusal(actor, Target("me", [isFullAdministrator ? BuiltInRoles.EverythingName : "usermanager"]), UserAction.Reset, null, null);

        // Assert
        Assert.Equal(UserDelegation.OwnPasswordResetRefusal, refusal);
    }

    [Theory]
    [InlineData(nameof(UserAction.Remove))]
    [InlineData(nameof(UserAction.Lock))]
    [InlineData(nameof(UserAction.SetRoles))]
    public void WhenAFullAdministratorChangesTheirOwnAccountOtherwise_ThenTheDelegationRulesDoNotRefuseIt(string actionName)
    {
        // Arrange
        var action = Enum.Parse<UserAction>(actionName);

        // Act
        var refusal = UserDelegation.GetRefusal(
            Actor(Closure(BuiltInRoles.EverythingName), "me"), Target("me", [BuiltInRoles.EverythingName]), action,
            action == UserAction.SetRoles ? [BuiltInRoles.ReaderName] : null, action == UserAction.SetRoles ? Closure(BuiltInRoles.ReaderName) : null);

        // Assert
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(nameof(UserAction.Remove))]
    [InlineData(nameof(UserAction.Lock))]
    [InlineData(nameof(UserAction.Unlock))]
    [InlineData(nameof(UserAction.Reset))]
    [InlineData(nameof(UserAction.SetRoles))]
    public void WhenTheTargetHoldsARoleTheManagerDoesNot_ThenEveryActionIsRefused(string actionName)
    {
        // Arrange
        var action = Enum.Parse<UserAction>(actionName);

        // Act
        var refusal = UserDelegation.GetRefusal(
            Actor(ManagerClosure), Target("admin", [BuiltInRoles.EverythingName]), action, action == UserAction.SetRoles ? ["family"] : null, action == UserAction.SetRoles ? Closure("family") : null);

        // Assert
        Assert.NotNull(refusal);
    }

    [Fact]
    public void WhenTheTargetIsABuiltInUserAndTheActorIsNotAFullAdministrator_ThenItIsRefused()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(Actor(ManagerClosure), Target("anonymous-id", [], isBuiltIn: true), UserAction.SetRoles, ["family"], Closure("family"));

        // Assert
        Assert.NotNull(refusal);
    }

    [Fact]
    public void WhenTheActorIsAFullAdministrator_ThenDelegationRulesDoNotApply()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(
            Actor(Closure(BuiltInRoles.EverythingName)), Target("anonymous-id", [], isBuiltIn: true), UserAction.SetRoles, ["guest"], Closure("guest"));

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleAssignsAnIncludedRoleAlone_ThenItIsRefusedWithTheDenyingRole()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(
            KidManager(), Target("peer", []), UserAction.SetRoles, [BuiltInRoles.OperatorName], Closure(BuiltInRoles.OperatorName, BuiltInRoles.ReaderName));

        // Assert
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", refusal);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleAssignsItTogetherWithAnIncludedRole_ThenItIsAllowed()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(
            KidManager(), Target("peer", []), UserAction.SetRoles, [BuiltInRoles.OperatorName, "kid"], Closure(BuiltInRoles.OperatorName, "kid", BuiltInRoles.ReaderName));

        // Assert
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(nameof(UserAction.Reset))]
    [InlineData(nameof(UserAction.Unlock))]
    [InlineData(nameof(UserAction.SetRoles))]
    public void WhenAManagerWithADenyingRoleActsOnAPeerWithoutIt_ThenItIsRefusedWithTheDenyingRole(string actionName)
    {
        // Arrange
        var action = Enum.Parse<UserAction>(actionName);

        // Act
        var refusal = UserDelegation.GetRefusal(
            KidManager(), TargetIncluding("peer", [BuiltInRoles.OperatorName], BuiltInRoles.ReaderName), action,
            action == UserAction.SetRoles ? ["kid"] : null, action == UserAction.SetRoles ? KidClosure() : null);

        // Assert
        Assert.Equal("This user does not hold your roles with deny rules: kid.", refusal);
    }

    [Theory]
    [InlineData(nameof(UserAction.Lock))]
    [InlineData(nameof(UserAction.Remove))]
    public void WhenAManagerWithADenyingRoleTakesAccessAwayFromAPeerWithoutIt_ThenItIsAllowed(string actionName)
    {
        // Arrange
        var action = Enum.Parse<UserAction>(actionName);

        // Act
        var refusal = UserDelegation.GetRefusal(KidManager(), TargetIncluding("peer", [BuiltInRoles.OperatorName], BuiltInRoles.ReaderName), action, null, null);

        // Assert
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(BuiltInRoles.ReaderName)]
    [InlineData(BuiltInRoles.OperatorName)]
    [InlineData(null)]
    public void WhenAManagerWithADenyingRoleOnlyRemovesRolesOfAPeerWithoutIt_ThenItIsAllowed(string? remainingRole)
    {
        // Arrange
        string[] newRoles = remainingRole is null ? [] : [remainingRole];
        var newClosure = remainingRole == BuiltInRoles.OperatorName ? Closure(BuiltInRoles.OperatorName, BuiltInRoles.ReaderName) : Closure(newRoles);

        // Act
        var refusal = UserDelegation.GetRefusal(
            KidManager(), TargetIncluding("peer", [BuiltInRoles.OperatorName], BuiltInRoles.ReaderName), UserAction.SetRoles, newRoles, newClosure);

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleStripsItFromAPeerHoldingIt_ThenItIsRefusedWithTheDenyingRole()
    {
        // Arrange
        var alt = TargetIncluding("alt", ["kid"], BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);
        var peer = TargetIncluding("peer", ["kid", BuiltInRoles.OperatorName], BuiltInRoles.ReaderName);
        var operatorClosure = Closure(BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);

        // Act
        var altToOperator = UserDelegation.GetRefusal(KidManager(), alt, UserAction.SetRoles, [BuiltInRoles.OperatorName], operatorClosure);
        var peerToOperator = UserDelegation.GetRefusal(KidManager(), peer, UserAction.SetRoles, [BuiltInRoles.OperatorName], operatorClosure);

        // Assert
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", altToOperator);
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", peerToOperator);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleRemovesRolesOfAPeerHoldingItButKeepsIt_ThenItIsAllowed()
    {
        // Arrange
        var peer = TargetIncluding("peer", ["kid", BuiltInRoles.OperatorName], BuiltInRoles.ReaderName);

        // Act
        var toKid = UserDelegation.GetRefusal(KidManager(), peer, UserAction.SetRoles, ["kid"], KidClosure());
        var toNone = UserDelegation.GetRefusal(KidManager(), peer, UserAction.SetRoles, [], Closure());

        // Assert
        Assert.Null(toKid);
        Assert.Null(toNone);
    }

    [Fact]
    public void WhenAFullAdministratorWithADenyingRoleDropsItFromTheirOwnRoles_ThenItIsRefused()
    {
        // Arrange
        var closure = Closure(BuiltInRoles.EverythingName, "kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);
        var administrator = new UserActor("admin", closure, true, Closure("kid"));
        var self = new UserTarget("admin", false, [BuiltInRoles.EverythingName, "kid"], closure);

        // Act
        var refusal = UserDelegation.GetRefusal(
            administrator, self, UserAction.SetRoles, [BuiltInRoles.EverythingName], Closure(BuiltInRoles.EverythingName));

        // Assert
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", refusal);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleGrantsARoleToAPeerWithoutIt_ThenItIsRefusedWithTheDenyingRole()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(
            KidManager(), Target("peer", [BuiltInRoles.ReaderName]), UserAction.SetRoles,
            [BuiltInRoles.OperatorName], Closure(BuiltInRoles.OperatorName, BuiltInRoles.ReaderName));

        // Assert
        Assert.Equal("This user does not hold your roles with deny rules: kid.", refusal);
    }

    [Fact]
    public void WhenAFullAdministratorWithADenyingRoleRemovesRolesOfABuiltInUser_ThenItIsAllowed()
    {
        // Arrange
        var administrator = new UserActor("admin", Closure(BuiltInRoles.EverythingName, "kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName), true, Closure("kid"));
        var mcp = new UserTarget("mcp-id", true, [BuiltInRoles.OperatorName], Closure(BuiltInRoles.OperatorName, BuiltInRoles.ReaderName));

        // Act
        var toReader = UserDelegation.GetRefusal(administrator, mcp, UserAction.SetRoles, [BuiltInRoles.ReaderName], Closure(BuiltInRoles.ReaderName));
        var toNone = UserDelegation.GetRefusal(administrator, mcp, UserAction.SetRoles, [], Closure());
        var toEverything = UserDelegation.GetRefusal(
            administrator, mcp, UserAction.SetRoles, [BuiltInRoles.EverythingName], Closure(BuiltInRoles.EverythingName));

        // Assert
        Assert.Null(toReader);
        Assert.Null(toNone);
        Assert.Equal("This user does not hold your roles with deny rules: kid.", toEverything);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleResetsAPeerHoldingIt_ThenItIsAllowed()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(KidManager(), TargetIncluding("peer", ["kid"], BuiltInRoles.OperatorName, BuiltInRoles.ReaderName), UserAction.Reset, null, null);

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAManagerWithoutDenyingRolesAssignsAnIncludedRoleAlone_ThenItIsAllowed()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(
            Actor(ManagerClosure), TargetIncluding("peer", [BuiltInRoles.OperatorName], BuiltInRoles.ReaderName), UserAction.SetRoles,
            [BuiltInRoles.ReaderName], Closure(BuiltInRoles.ReaderName));

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenAFullAdministratorWithADenyingRoleAssignsRolesWithoutIt_ThenItIsRefused()
    {
        // Arrange
        var administrator = new UserActor("admin", Closure(BuiltInRoles.EverythingName, "kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName), true, Closure("kid"));

        // Act
        var assignWithout = UserDelegation.GetRefusal(
            administrator, Target("peer", []), UserAction.SetRoles, [BuiltInRoles.EverythingName], Closure(BuiltInRoles.EverythingName));
        var resetWithout = UserDelegation.GetRefusal(administrator, TargetIncluding("peer", [BuiltInRoles.OperatorName], BuiltInRoles.ReaderName), UserAction.Reset, null, null);
        var assignWith = UserDelegation.GetRefusal(administrator, Target("peer", []), UserAction.SetRoles, ["kid"], KidClosure());

        // Assert
        Assert.Equal("The assigned roles must include your roles with deny rules: kid.", assignWithout);
        Assert.Equal("This user does not hold your roles with deny rules: kid.", resetWithout);
        Assert.Null(assignWith);
    }

    [Fact]
    public void WhenAManagerWithADenyingRoleActsOnAUserWithoutRoles_ThenItIsAllowed()
    {
        // Act
        var refusal = UserDelegation.GetRefusal(KidManager(), Target("peer", []), UserAction.Reset, null, null);

        // Assert
        Assert.Null(refusal); // a user without roles may do nothing
    }

    private static UserActor KidManager() => new("manager", KidManagerClosure, false, Closure("kid"));

    private static IReadOnlySet<string> KidClosure() => Closure("kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName);

    private static UserActor Actor(IReadOnlySet<string> closure, string id = "manager") =>
        new(id, closure, closure.Contains(BuiltInRoles.EverythingName), Closure());

    private static UserTarget Target(string id, string[] roles, bool isBuiltIn = false) =>
        new(id, isBuiltIn, roles, Closure(roles));

    private static UserTarget TargetIncluding(string id, string[] roles, params string[] includedRoles) =>
        new(id, false, roles, Closure([.. roles, .. includedRoles]));

    private static IReadOnlySet<string> Closure(params string[] names) => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
}
