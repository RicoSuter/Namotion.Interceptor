using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ControlLabelTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    [Fact]
    public void WhenLabelIsGiven_ThenItIsTheLabel()
    {
        // Arrange
        var card = new Card(_context);
        var property = card.TryGetRegisteredSubject()!.TryGetProperty(nameof(Card.Title));

        // Act
        var label = ValueReader.GetControlLabel(property, "Pump", "card/Title");

        // Assert
        Assert.Equal("Pump", label);
    }

    [Fact]
    public void WhenNoLabelIsGiven_ThenItIsTheNameOfTheProperty()
    {
        // Arrange
        var card = new Card(_context);
        var property = card.TryGetRegisteredSubject()!.TryGetProperty(nameof(Card.Title));

        // Act
        var label = ValueReader.GetControlLabel(property, null, "card/Title");

        // Assert
        Assert.Equal("Title", label);
    }

    [Fact]
    public void WhenThePathLeadsNowhere_ThenItIsThePath()
    {
        // Act
        var label = ValueReader.GetControlLabel(null, "", "pump/Missing");

        // Assert
        Assert.Equal("pump/Missing", label);
    }
}
