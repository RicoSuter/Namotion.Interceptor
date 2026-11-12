using Xunit;

namespace Namotion.Interceptor.Authorization.Tests;

public class PermissionRuleTextTests
{
    [Fact]
    public void WhenTheRuleHasNoSelectors_ThenItsTextListsTheActions()
    {
        // Arrange
        var rule = new PermissionRule { Actions = [PermissionAction.Read, PermissionAction.Write] };

        // Act
        var text = rule.ToString();

        // Assert
        Assert.Equal("read, write", text);
    }

    [Fact]
    public void WhenTheRuleHasSelectors_ThenItsTextListsEachOneThatIsNotEmptyAfterTheActions()
    {
        // Arrange
        var rule = new PermissionRule
        {
            Paths = ["/Devices/**", "/Kitchen/*"],
            Members = ["Is*"],
            Tags = ["query"],
            Actions = [PermissionAction.Read]
        };

        // Act
        var text = rule.ToString();

        // Assert
        Assert.Equal("read · paths: /Devices/**, /Kitchen/* · members: Is* · tags: query", text);
    }
}
