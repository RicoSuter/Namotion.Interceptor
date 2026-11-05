using HomeBlaze.Authentication.Internal;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class CommitResultTests
{
    [Fact]
    public void WhenAResultWithASetupCodeAndAKeyIsPrinted_ThenNeitherSecretShows()
    {
        // Arrange
        var result = new CommitResult(null, "SETUP-CODE-1234", "user-id", "hb_secret-key");

        // Act
        var text = result.ToString();

        // Assert
        Assert.DoesNotContain("SETUP-CODE-1234", text);
        Assert.DoesNotContain("hb_secret-key", text);
        Assert.Contains("user-id", text);
    }
}
