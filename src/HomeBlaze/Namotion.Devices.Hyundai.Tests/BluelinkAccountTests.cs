using Namotion.Devices.Hyundai.Tests.Testing;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkAccountTests
{
    [Theory]
    [InlineData("Garage", "user@example.invalid", "Garage")]
    [InlineData("", "user@example.invalid", "Hyundai: user@example.invalid")]
    [InlineData("", "", "Hyundai")]
    public void WhenNameAndEmailAreSet_ThenTitleUsesTheNameElseTheEmail(string name, string email, string expectedTitle)
    {
        // Arrange
        var (account, _, _) = TestAccount.Create();

        // Act
        account.Name = name;
        account.Email = email;

        // Assert
        Assert.Equal(expectedTitle, account.Title);
    }
}
