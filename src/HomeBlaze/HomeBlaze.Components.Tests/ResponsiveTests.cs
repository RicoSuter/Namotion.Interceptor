using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using Namotion.Interceptor;
using Namotion.Interceptor.Registry;
using Namotion.Interceptor.Tracking;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class ResponsiveTests
{
    private readonly IInterceptorSubjectContext _context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry();

    [Theory]
    [InlineData(ResponsiveShow.Phone, "responsive-widget d-block d-sm-none")]
    [InlineData(ResponsiveShow.Tablet, "responsive-widget d-none d-sm-block d-lg-none")]
    [InlineData(ResponsiveShow.Desktop, "responsive-widget d-none d-lg-block")]
    [InlineData(ResponsiveShow.NotPhone, "responsive-widget d-none d-sm-block")]
    [InlineData(ResponsiveShow.NotDesktop, "responsive-widget d-block d-lg-none")]
    public void WhenScreensAreChosen_ThenTheDisplayClassesHideTheContentOnTheOthers(ResponsiveShow show, string expected)
    {
        // Act
        var classes = Responsive.GetCssClasses(show);

        // Assert
        Assert.Equal(expected, classes);
    }

    [Fact]
    public void WhenResponsiveIsCreated_ThenItShowsItsContentOnAllScreensButPhonesInItsDefaultSlot()
    {
        // Act
        var responsive = new Responsive(_context);
        var slot = typeof(Responsive).GetProperty(nameof(Responsive.Content))!.GetCustomAttribute<SlotAttribute>()!;

        // Assert
        Assert.Equal(ResponsiveShow.NotPhone, responsive.Show);
        Assert.True(slot.IsDefault);
    }
}
