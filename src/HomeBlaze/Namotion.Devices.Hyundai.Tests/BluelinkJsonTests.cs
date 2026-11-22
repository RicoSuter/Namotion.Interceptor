using System.Text.Json;
using Namotion.Devices.Hyundai.Client;

namespace Namotion.Devices.Hyundai.Tests;

public class BluelinkJsonTests
{
    [Fact]
    public void WhenPathExists_ThenNumbersAndNumericStringsAreRead()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"A":{"B":{"Number":46.5,"Text":"22.0","Off":"OFF","Flag":1}}}""");
        var root = document.RootElement;

        // Act & Assert
        Assert.Equal(46.5m, root.GetDecimal("A.B.Number"));
        Assert.Equal(22.0m, root.GetDecimal("A.B.Text"));
        Assert.Null(root.GetDecimal("A.B.Off"));
        Assert.Null(root.GetDecimal("A.Missing.Number"));
        Assert.Equal(1, root.GetInt32("A.B.Flag"));
        Assert.Equal("OFF", root.GetString("A.B.Off"));
    }

    [Fact]
    public void WhenObjectIsWritten_ThenItIsCompactJson()
    {
        // Act
        var json = BluelinkJson.Write(writer =>
        {
            writer.WriteString("action", "start");
            writer.WriteNumber("value", 3);
        });

        // Assert
        Assert.Equal("""{"action":"start","value":3}""", json);
    }

    [Fact]
    public void WhenFlagIsRead_ThenZeroOneAndBooleansMapAndOtherValuesAreNull()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"Zero":0,"One":1,"True":true,"False":false,"Two":2,"Text":"x"}""");
        var root = document.RootElement;

        // Act & Assert
        Assert.False(root.GetFlag("Zero"));
        Assert.True(root.GetFlag("One"));
        Assert.True(root.GetFlag("True"));
        Assert.False(root.GetFlag("False"));
        Assert.Null(root.GetFlag("Two"));
        Assert.Null(root.GetFlag("Text"));
        Assert.Null(root.GetFlag("Missing"));
    }

    [Fact]
    public void WhenValueIsNotAString_ThenGetStringReturnsRawTextForNumbersAndNullOtherwise()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"Number":46.50,"Flag":true,"Object":{},"Array":[1]}""");
        var root = document.RootElement;

        // Act & Assert
        Assert.Equal("46.50", root.GetString("Number"));
        Assert.Null(root.GetString("Flag"));
        Assert.Null(root.GetString("Object"));
        Assert.Null(root.GetString("Array"));
    }

    [Fact]
    public void WhenNumberHasFraction_ThenGetInt32ReturnsNull()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"Value":1.5,"Large":3000000000}""");
        var root = document.RootElement;

        // Act & Assert
        Assert.Null(root.GetInt32("Value"));
        Assert.Null(root.GetInt32("Large"));
    }

    [Fact]
    public void WhenPathPassesNullOrArray_ThenFindReturnsNull()
    {
        // Arrange
        using var document = JsonDocument.Parse("""{"A":null,"B":[{"C":1}]}""");
        var root = document.RootElement;

        // Act & Assert
        Assert.Null(root.Find("A"));
        Assert.Null(root.Find("A.C"));
        Assert.Null(root.Find("B.C"));
        Assert.Null(root.Find("B.0.C"));
    }
}
