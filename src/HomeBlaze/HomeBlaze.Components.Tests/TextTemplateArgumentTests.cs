using System.Reflection;
using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Components.Tests;

public class TextTemplateArgumentTests
{
    [Fact]
    public void WhenWidgetTypesAreScanned_ThenTheLabelsTitlesAndSubtitlesOfTheValueWidgetsAndCardsAreTemplates()
    {
        // Act
        var templates = typeof(ValueTile).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.IsDefined(typeof(TextTemplateAttribute), inherit: true))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(
            [
                "Card.Subtitle", "Card.Title", "Control.Label", "Gauge.Label", "Switch.Label", "Switch.Subtitle",
                "ValueMapCase.Text", "ValueRow.Label", "ValueTile.Label", "ValueTile.Subtitle"
            ],
            templates);
    }

    [Fact]
    public void WhenArgumentIsATemplate_ThenItIsAStringConfigurationProperty()
    {
        // Act
        var wrong = typeof(ValueTile).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(property => property.IsDefined(typeof(TextTemplateAttribute), inherit: true) &&
                               (property.PropertyType != typeof(string) || !property.IsDefined(typeof(ConfigurationAttribute), inherit: true)))
            .Select(property => $"{property.DeclaringType!.Name}.{property.Name}");

        // Assert
        Assert.Empty(wrong);
    }
}
