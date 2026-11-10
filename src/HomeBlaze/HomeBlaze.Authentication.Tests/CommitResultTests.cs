using HomeBlaze.Authentication.Internal;
using Xunit;

namespace HomeBlaze.Authentication.Tests;

public class CommitResultTests
{
    [Fact]
    public void WhenAResultWithASetupCodeAKeyAndAUserIsPrinted_ThenNoSecretShows()
    {
        // Arrange
        var user = new UserEntry { Id = "user-id", UserName = "mom", Roles = Array.AsReadOnly(Array.Empty<string>()), SecurityStamp = "secret-stamp" };
        var result = new CommitResult(null, "SETUP-CODE-1234", "hb_secret-key", user);

        // Act
        var text = result.ToString();

        // Assert
        Assert.DoesNotContain("SETUP-CODE-1234", text);
        Assert.DoesNotContain("hb_secret-key", text);
        Assert.DoesNotContain("secret-stamp", text);
        Assert.Contains("user-id", text);
    }
}
