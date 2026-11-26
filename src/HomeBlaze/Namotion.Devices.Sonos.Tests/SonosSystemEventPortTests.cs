using Microsoft.Extensions.Logging.Abstractions;
using Namotion.Devices.Sonos.Tests.Testing;
using Namotion.Interceptor;
using Xunit;

namespace Namotion.Devices.Sonos.Tests;

public class SonosSystemEventPortTests
{
    private static SonosSystem CreateSystem(int eventPort = SonosSystem.DefaultEventPort) =>
        new(new TestHttpClientFactory(), NullLogger<SonosSystem>.Instance) { EventPort = eventPort };

    [Fact]
    public void WhenAnotherSystemUsesThePort_ThenItIsFound()
    {
        // Arrange
        var existing = CreateSystem();
        IInterceptorSubject[] subjects = [CreateSystem(eventPort: 6330), existing];

        // Act
        var conflict = SonosSystem.FindSystemUsingEventPort(subjects, SonosSystem.DefaultEventPort, system: null);

        // Assert
        Assert.Same(existing, conflict);
    }

    [Fact]
    public void WhenOnlyTheEditedSystemUsesThePort_ThenNoneIsFound()
    {
        // Arrange
        var edited = CreateSystem();
        IInterceptorSubject[] subjects = [edited, CreateSystem(eventPort: 6330)];

        // Act
        var conflict = SonosSystem.FindSystemUsingEventPort(subjects, SonosSystem.DefaultEventPort, edited);

        // Assert
        Assert.Null(conflict);
    }

    [Fact]
    public void WhenNoOtherSystemUsesThePort_ThenNoneIsFound()
    {
        // Arrange
        IInterceptorSubject[] subjects = [CreateSystem(eventPort: 6330)];

        // Act
        var conflict = SonosSystem.FindSystemUsingEventPort(subjects, SonosSystem.DefaultEventPort, system: null);

        // Assert
        Assert.Null(conflict);
    }
}
