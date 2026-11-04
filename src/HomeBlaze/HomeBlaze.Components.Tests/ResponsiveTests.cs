using System.Reflection;
using HomeBlaze.Abstractions.Attributes;
using MudBlazor;
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
    [InlineData(Breakpoint.Xs, Breakpoint.Xxl, "responsive-widget d-block")]
    [InlineData(Breakpoint.Xs, Breakpoint.Xs, "responsive-widget d-block d-sm-none")]
    [InlineData(Breakpoint.Sm, Breakpoint.Md, "responsive-widget d-none d-sm-block d-lg-none")]
    [InlineData(Breakpoint.Md, Breakpoint.Md, "responsive-widget d-none d-md-block d-lg-none")]
    [InlineData(Breakpoint.Lg, Breakpoint.Xxl, "responsive-widget d-none d-lg-block")]
    [InlineData(Breakpoint.Sm, Breakpoint.Xxl, "responsive-widget d-none d-sm-block")]
    [InlineData(Breakpoint.Xs, Breakpoint.Md, "responsive-widget d-block d-lg-none")]
    [InlineData(Breakpoint.Xl, Breakpoint.Xl, "responsive-widget d-none d-xl-block d-xxl-none")]
    [InlineData(Breakpoint.Xxl, Breakpoint.Xxl, "responsive-widget d-none d-xxl-block")]
    public void WhenBreakpointsAreChosen_ThenTheDisplayClassesHideTheContentOutsideOfThem(Breakpoint from, Breakpoint to, string expected)
    {
        // Act
        var isValid = Responsive.TryGetCssClasses(from, to, out var cssClasses, out var error);

        // Assert
        Assert.True(isValid);
        Assert.Equal(expected, cssClasses);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(Breakpoint.SmAndUp, Breakpoint.Xxl, "From must be one of Xs, Sm, Md, Lg, Xl, Xxl: SmAndUp")]
    [InlineData(Breakpoint.Always, Breakpoint.Xxl, "From must be one of Xs, Sm, Md, Lg, Xl, Xxl: Always")]
    [InlineData(Breakpoint.Xs, Breakpoint.None, "To must be one of Xs, Sm, Md, Lg, Xl, Xxl: None")]
    [InlineData(Breakpoint.Xs, Breakpoint.MdAndDown, "To must be one of Xs, Sm, Md, Lg, Xl, Xxl: MdAndDown")]
    [InlineData(Breakpoint.Lg, Breakpoint.Sm, "From must not be after To: Lg to Sm")]
    public void WhenABreakpointIsCombinedOrFromIsAfterTo_ThenItIsAnArgumentError(Breakpoint from, Breakpoint to, string expected)
    {
        // Act
        var isValid = Responsive.TryGetCssClasses(from, to, out var cssClasses, out var error);

        // Assert
        Assert.False(isValid);
        Assert.Null(cssClasses);
        Assert.Equal(expected, error);
    }

    public static TheoryData<Breakpoint, Breakpoint> EveryRange()
    {
        var data = new TheoryData<Breakpoint, Breakpoint>();
        for (var from = Breakpoint.Xs; from <= Breakpoint.Xxl; from++)
        {
            for (var to = from; to <= Breakpoint.Xxl; to++)
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryRange))]
    public void WhenTheHiddenClassesOfAnyRangeAreAskedFor_ThenTheyShowAnElementExactlyWhereTheContentIsHidden(Breakpoint from, Breakpoint to)
    {
        // Arrange: a class of a breakpoint applies from its lower edge, so a class is needed only where that changes.
        string[] names = ["xs", "sm", "md", "lg", "xl", "xxl"];
        string? expected = null;
        if (from != Breakpoint.Xs || to != Breakpoint.Xxl)
        {
            var isShown = from > Breakpoint.Xs;
            expected = isShown ? "d-block" : "d-none";
            for (var breakpoint = 1; breakpoint < names.Length; breakpoint++)
            {
                var isContentHidden = breakpoint < (int)from || breakpoint > (int)to;
                if (isContentHidden != isShown)
                {
                    expected += $" d-{names[breakpoint]}{(isContentHidden ? "-block" : "-none")}";
                    isShown = isContentHidden;
                }
            }
        }

        // Act
        var classes = Responsive.GetHiddenCssClasses(from, to);

        // Assert
        Assert.Equal(expected, classes);
    }

    [Theory]
    [InlineData(Breakpoint.Xs, Breakpoint.Xs, "Xs only")]
    [InlineData(Breakpoint.Xs, Breakpoint.Md, "Up to Md")]
    [InlineData(Breakpoint.Lg, Breakpoint.Xxl, "Lg and up")]
    [InlineData(Breakpoint.Sm, Breakpoint.Md, "Sm to Md")]
    public void WhenTheRangeLabelIsAskedFor_ThenItNamesTheBreakpointsThatShowTheContent(Breakpoint from, Breakpoint to, string expected)
    {
        // Act & Assert
        Assert.Equal(expected, Responsive.GetRangeLabel(from, to));
    }

    [Fact]
    public void WhenResponsiveIsCreated_ThenItShowsItsContentAtAllWidthsInItsDefaultSlot()
    {
        // Act
        var responsive = new Responsive(_context);
        var slot = typeof(Responsive).GetProperty(nameof(Responsive.Content))!.GetCustomAttribute<SlotAttribute>()!;

        // Assert
        Assert.Equal((Breakpoint.Xs, Breakpoint.Xxl), (responsive.From, responsive.To));
        Assert.True(slot.IsDefault);
    }
}
