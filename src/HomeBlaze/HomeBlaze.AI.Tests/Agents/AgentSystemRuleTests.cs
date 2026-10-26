using HomeBlaze.AI.Agents;
using HomeBlaze.Authorization;
using Xunit;

namespace HomeBlaze.AI.Tests.Agents;

public class AgentSystemRuleTests
{
    [Fact]
    public void WhenTheSystemRulesNameTheAgentBaseType_ThenTheNameIsTheFullNameOfTheType()
    {
        // Act
        var fullName = typeof(LlmAgentBase).FullName;

        // Assert: the rule names the type by a string, so a rename of the type would silently disable it.
        Assert.Equal(SystemRules.LlmAgentBaseTypeName, fullName);
    }
}
