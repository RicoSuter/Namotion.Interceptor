using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkStampTests
{
    [Fact]
    public void WhenStampIsCreated_ThenItMatchesThePythonReference()
    {
        // Arrange
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);

        // Act
        var stamp = BluelinkStamp.Create(now);

        // Assert
        Assert.Equal("dGpcIp3dP0dLfkNRUItk/sCdmouaEMW4JAlLv76lGxrWLqMbn3rSYiqWOyDGh9A=", stamp);
    }
}
