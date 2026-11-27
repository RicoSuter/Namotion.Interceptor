using System.Text.Json;
using HomeBlaze.Game.Components;

namespace HomeBlaze.Game.Tests;

public class GameGraphicsTests
{
    private static readonly GameGraphics CustomGraphics = new(
        GameGraphics.Custom, GameGraphics.Custom, 1.5, 0, "off", false, 0.5, true, false, true, false);

    [Theory]
    [InlineData("auto")]
    [InlineData("low")]
    [InlineData("high")]
    public void WhenThePresetIsNotCustom_ThenThePreferenceHoldsOnlyThePreset(string preset)
    {
        // Arrange
        var graphics = CustomGraphics with { Preset = preset, Level = "medium" };

        // Act
        var preference = graphics.ToPreference();

        // Assert
        Assert.Equal($$"""{"preset":"{{preset}}"}""", preference);
    }

    [Fact]
    public void WhenThePresetIsCustom_ThenThePreferenceHoldsEverySettingButTheLevel()
    {
        // Act
        var preference = CustomGraphics.ToPreference();

        // Assert
        Assert.Equal(
            """{"preset":"custom","resolution":1.5,"frameCap":0,"shadows":"off","outlines":false,"ambientOcclusion":0.5,"multisampling":true,"sharpen":false,"wind":true,"fade":false}""",
            preference);
    }

    [Fact]
    public void WhenTheModuleReportsGraphics_ThenTheyReadBackAsTheSameRecord()
    {
        // Arrange
        // The shape game-render.js returns, with the level that the preference leaves out.
        var json = CustomGraphics.ToPreference().Replace("\"preset\":\"custom\"", "\"preset\":\"custom\",\"level\":\"custom\"", StringComparison.Ordinal);

        // Act
        var graphics = JsonSerializer.Deserialize<GameGraphics>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        // Assert
        Assert.Equal(CustomGraphics, graphics);
    }
}
