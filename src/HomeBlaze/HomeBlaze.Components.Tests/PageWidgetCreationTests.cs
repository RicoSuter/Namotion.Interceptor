using HomeBlaze.Services;
using Xunit;

namespace HomeBlaze.Components.Tests;

public class PageWidgetCreationTests
{
    private static SubjectTypeCatalog CreateCatalog()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(Card), typeof(FlowLayout), typeof(TabLayout), typeof(Tab)]);
        return new SubjectTypeCatalog(new SubjectTypeRegistry(typeProvider));
    }

    [Fact]
    public void WhenTypesAreOfferedForCreation_ThenTheWidgetsAreOfferedAndATabIsNot()
    {
        // Arrange
        var catalog = CreateCatalog();

        // Act
        var descriptors = catalog.GetCreatableTypes([]);

        // Assert - a tab only exists inside a tab layout.
        Assert.Equal(
            [typeof(Card), typeof(FlowLayout), typeof(TabLayout)],
            descriptors.Select(descriptor => descriptor.Type));
    }

    [Theory]
    [InlineData(typeof(Card), "Card")]
    [InlineData(typeof(FlowLayout), "Flow layout")]
    [InlineData(typeof(TabLayout), "Tab layout")]
    public void WhenWidgetIsDescribed_ThenItIsInTheDashboardGroupWithAnIconOfItsOwn(Type type, string expectedDisplayName)
    {
        // Arrange
        var fallbackIcon = MudBlazorIconResolver.Resolve(null);
        var groupIcon = MudBlazorIconResolver.Resolve(SubjectTypeCatalog.GroupIconNames["Dashboard"]);

        // Act
        var descriptor = SubjectTypeCatalog.Describe(type);
        var icon = MudBlazorIconResolver.Resolve(descriptor.IconName);

        // Assert - an unknown icon name silently shows the fallback icon instead of failing.
        Assert.Equal("Dashboard", descriptor.Group);
        Assert.Equal(expectedDisplayName, descriptor.DisplayName);
        Assert.NotNull(descriptor.Description);
        Assert.NotEqual(fallbackIcon, icon);
        Assert.NotEqual(groupIcon, icon);
    }
}
