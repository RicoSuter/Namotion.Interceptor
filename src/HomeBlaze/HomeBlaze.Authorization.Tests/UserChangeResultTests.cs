using Xunit;

namespace HomeBlaze.Authorization.Tests;

public class UserChangeResultTests
{
    [Fact]
    public void WhenAResultWithASetupCodeAndAKeyIsPrinted_ThenNeitherSecretShows()
    {
        // Arrange
        var result = new UserChangeResult(true, null, "SETUP-CODE-1234", "hb_secret-key");

        // Act
        var text = result.ToString();

        // Assert
        Assert.DoesNotContain("SETUP-CODE-1234", text);
        Assert.DoesNotContain("hb_secret-key", text);
        Assert.Contains("Succeeded = True", text);
    }
}
