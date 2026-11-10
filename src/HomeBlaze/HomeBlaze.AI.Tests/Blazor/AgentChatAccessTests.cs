using HomeBlaze.AI.Agents;
using HomeBlaze.AI.Blazor;
using HomeBlaze.AI.Tests.Agents;
using HomeBlaze.Authorization;
using HomeBlaze.Components.Tests.Access;
using Xunit;

namespace HomeBlaze.AI.Tests.Blazor;

public class AgentChatAccessTests
{
    [Fact]
    public void WhenThePersonHoldsEveryRoleOfTheAgent_ThenTheyMayChat()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = [BuiltInRoles.ReaderName] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.OperatorName, BuiltInRoles.ReaderName] }, agent);

        // Assert
        Assert.Null(refusal); // Operator includes Reader
    }

    [Fact]
    public void WhenThePersonLacksARoleOfTheAgent_ThenChatIsRefusedWithTheRoleName()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = [BuiltInRoles.OperatorName] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.ReaderName] }, agent);

        // Assert
        Assert.NotNull(refusal);
        Assert.Contains(BuiltInRoles.OperatorName, refusal);
    }

    [Fact]
    public void WhenThePersonHoldsARoleWithDenyRulesTheAgentLacks_ThenChatIsRefusedWithThatRole()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = [BuiltInRoles.OperatorName] };
        var kid = new TestUserPermissions { HeldRoles = ["kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName], RolesWithDenyRules = ["kid"] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(kid, agent);

        // Assert
        Assert.Equal("You can chat with this agent only if it holds your roles with deny rules, so that it cannot do what they deny you. Missing: kid.", refusal);
    }

    [Fact]
    public void WhenTheAgentHoldsTheRoleWithDenyRulesOfThePerson_ThenTheyMayChat()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = ["kid"] };
        var kid = new TestUserPermissions { HeldRoles = ["kid", BuiltInRoles.OperatorName, BuiltInRoles.ReaderName], RolesWithDenyRules = ["kid"] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(kid, agent);

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenThePersonIsAFullAdministrator_ThenTheyMayChatWithAnyAgent()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = ["some-custom-role"] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.EverythingName] }, agent);

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void WhenTheRoleNamesDifferOnlyInCase_ThenThePersonMayNotChat()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = ["Operator"] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.OperatorName, BuiltInRoles.ReaderName] }, agent);

        // Assert
        Assert.NotNull(refusal);
        Assert.Contains("Missing: Operator", refusal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void WhenTheAgentHasNoRoles_ThenOnlyAFullAdministratorMayChat(string roleName)
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services);
        agent.Roles = roleName.Length == 0 ? [] : [roleName];

        // Act
        var personRefusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.OperatorName, BuiltInRoles.ReaderName] }, agent);
        var administratorRefusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.EverythingName] }, agent);

        // Assert
        Assert.Equal("You can chat with this agent only if it has roles; ask a full administrator to assign them.", personRefusal);
        Assert.Null(administratorRefusal);
    }

    [Fact]
    public void WhenTheAgentListsARoleTwice_ThenTheRefusalNamesItOnce()
    {
        // Arrange
        var services = AgentTestContext.CreateServices(new AgentTestRoot());
        var agent = new FakeAgent(services) { Roles = ["custom", "custom"] };

        // Act
        var refusal = AgentChatAccess.GetRefusal(new TestUserPermissions { HeldRoles = [BuiltInRoles.ReaderName] }, agent);

        // Assert
        Assert.Equal("You can chat with this agent only if you hold its roles. Missing: custom.", refusal);
    }
}
