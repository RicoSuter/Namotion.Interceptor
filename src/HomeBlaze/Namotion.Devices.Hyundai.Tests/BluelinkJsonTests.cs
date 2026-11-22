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
}
