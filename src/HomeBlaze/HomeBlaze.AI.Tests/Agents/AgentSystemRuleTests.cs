using HomeBlaze.AI.Agents;
using HomeBlaze.Authorization;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentSystemRuleTests
{
    [Fact]
    public void WhenTheSystemRulesOfAgentsAreRead_ThenOneDeniesEveryActionOnTheAgentBaseType()
    {
        // Act
        var rules = SystemRules.For(PrincipalKind.NonHuman);

        // Assert: the rule names the type by a string, so a rename of the type would silently disable it.
        Assert.Contains(rules, rule => rule.Types.Contains(typeof(LlmAgentBase).FullName!) && rule.Actions.SequenceEqual(AuthorizationActions.All));
    }
}
