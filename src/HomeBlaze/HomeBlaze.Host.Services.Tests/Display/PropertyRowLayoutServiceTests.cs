using HomeBlaze.Host.Services.Display;

namespace HomeBlaze.Host.Services.Tests.Display;

public class PropertyRowLayoutServiceTests
{
    [Fact]
    public void WhenCreated_ThenLayoutIsAutomatic()
    {
        // Act
        var service = new PropertyRowLayoutService();

        // Assert
        Assert.Equal(PropertyRowLayout.Automatic, service.Layout);
    }

    [Fact]
    public void WhenLayoutIsChanged_ThenChangeIsRaised()
    {
        // Arrange
        var service = new PropertyRowLayoutService();
        var changes = 0;
        service.OnChange += () => changes++;

        // Act
        service.SetLayout(PropertyRowLayout.Stacked);

        // Assert
        Assert.Equal(PropertyRowLayout.Stacked, service.Layout);
        Assert.Equal(1, changes);
    }

    [Fact]
    public void WhenTheSameLayoutIsSet_ThenChangeIsNotRaised()
    {
        // Arrange
        var service = new PropertyRowLayoutService();
        service.SetLayout(PropertyRowLayout.SideBySide);
        var changes = 0;
        service.OnChange += () => changes++;

        // Act
        service.SetLayout(PropertyRowLayout.SideBySide);

        // Assert
        Assert.Equal(0, changes);
    }

    [Theory]
    [InlineData(PropertyRowLayout.Automatic)]
    [InlineData(PropertyRowLayout.SideBySide)]
    [InlineData(PropertyRowLayout.Stacked)]
    public void WhenStoredValueIsLoaded_ThenLayoutIsRestored(PropertyRowLayout layout)
    {
        // Arrange
        var stored = new PropertyRowLayoutService();
        stored.SetLayout(layout);
        var service = new PropertyRowLayoutService();

        // Act
        service.Load(stored.StoredValue);

        // Assert
        Assert.Equal(layout, service.Layout);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Columns")]
    [InlineData("stacked")]
    [InlineData("2")]
    public void WhenStoredValueIsMissingOrUnknown_ThenLayoutIsAutomatic(string? storedValue)
    {
        // Arrange
        var service = new PropertyRowLayoutService();
        service.SetLayout(PropertyRowLayout.Stacked);

        // Act
        service.Load(storedValue);

        // Assert
        Assert.Equal(PropertyRowLayout.Automatic, service.Layout);
    }
}
