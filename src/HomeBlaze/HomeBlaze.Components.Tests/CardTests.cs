using MudBlazor;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Parent;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class CardTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle();

    [Fact]
    public void WhenCardIsCreated_ThenItIsOutlinedWithTheDefaultElevation()
    {
        // Act
        var card = new Card(_context);

        // Assert
        Assert.Equal(2, card.Elevation);
        Assert.True(card.Outlined);
        Assert.Equal(Color.Default, card.IconColor);
        Assert.Null(card.HeaderRight);
    }
}
