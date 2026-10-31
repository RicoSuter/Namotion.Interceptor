using HomeBlaze.Game.Tests.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeBlaze.Game.Tests.Visuals;

public class GameVisualValidatorTests
{
    private static GameVisualPart CreatePart(string kind) => kind switch
    {
        "size" => new GameVisualPart { Size = new GameVisualVector(1025, 1, 1) },
        "negative-size" => new GameVisualPart { Size = new GameVisualVector(1, -1, 1) },
        "offset" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Offset = new GameVisualVector(0, 0, -1025) },
        "pivot" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Pivot = new GameVisualPoint(2000, 0) },
        "rotation" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Rotation = double.NaN },
        "texture" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Texture = "https://example.com/bricks.jpg" },
        "parent-texture" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Texture = "_content/A/../../secret.jpg" },
        "model" => new GameVisualPart { Shape = GameVisualShape.Model, Size = new GameVisualVector(1, 1, 1), Model = "models/tree.glb" },
        "missing-model" => new GameVisualPart { Shape = GameVisualShape.Model, Size = new GameVisualVector(1, 1, 1) },
        "color" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), Color = "red" },
        "texture-scale" => new GameVisualPart { Size = new GameVisualVector(1, 1, 1), TextureScale = 0 },
        "shape" => new GameVisualPart { Shape = (GameVisualShape)7, Size = new GameVisualVector(1, 1, 1) },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    [Fact]
    public void WhenTheVisualIsValid_ThenItIsKept()
    {
        // Arrange
        var gameObject = new VisualObject
        {
            CustomVisual = new GameVisual([new GameVisualPart
            {
                Shape = GameVisualShape.Model, Size = new GameVisualVector(20, 20, 20), Offset = new GameVisualVector(-9, 0, -9),
                Model = "_content/HomeBlaze.Game/objects/nature/models/pine_tree_1.glb", Effect = GameVisualEffect.Wind, Rotation = 270
            }])
        };

        // Act
        var visual = GameVisualValidator.GetValidVisual(gameObject, gameObject.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Same(gameObject.CustomVisual, visual);
    }

    [Theory]
    [InlineData("size")]
    [InlineData("negative-size")]
    [InlineData("offset")]
    [InlineData("pivot")]
    [InlineData("rotation")]
    [InlineData("texture")]
    [InlineData("parent-texture")]
    [InlineData("model")]
    [InlineData("missing-model")]
    [InlineData("color")]
    [InlineData("texture-scale")]
    [InlineData("shape")]
    public void WhenAPartIsInvalid_ThenTheObjectShowsAsTheDefaultBox(string kind)
    {
        // Arrange
        var gameObject = new VisualObject { Width = 2, Depth = 3, Height = 4, CustomVisual = new GameVisual([CreatePart(kind)]) };

        // Act
        var visual = GameVisualValidator.GetValidVisual(gameObject, gameObject.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Equal(GameVisual.Box(2, 4, 3), visual);
    }

    [Fact]
    public void WhenTheVisualHasTooManyParts_ThenTheObjectShowsAsTheDefaultBox()
    {
        // Arrange
        var part = new GameVisualPart { Size = new GameVisualVector(1, 1, 1) };
        var allowed = new VisualObject { CustomVisual = new GameVisual([.. Enumerable.Repeat(part, GameVisual.MaximumParts)]) };
        var tooMany = new VisualObject { CustomVisual = new GameVisual([.. Enumerable.Repeat(part, GameVisual.MaximumParts + 1)]) };

        // Act
        var allowedVisual = GameVisualValidator.GetValidVisual(allowed, allowed.CustomVisual, NullLogger.Instance);
        var tooManyVisual = GameVisualValidator.GetValidVisual(tooMany, tooMany.CustomVisual, NullLogger.Instance);

        // Assert
        Assert.Same(allowed.CustomVisual, allowedVisual);
        Assert.Equal(GameVisual.Box(1, 1, 1), tooManyVisual);
    }

    [Fact]
    public void WhenObjectsOfATypeHaveInvalidVisuals_ThenTheGameWarnsOnce()
    {
        // Arrange
        var logger = new RecordingLogger();
        var invalid = new GameVisual([CreatePart("texture")]);
        var first = new WarnedVisualObject { CustomVisual = invalid };
        var second = new WarnedVisualObject { CustomVisual = invalid };

        // Act
        GameVisualValidator.GetValidVisual(first, first.CustomVisual, logger);
        GameVisualValidator.GetValidVisual(second, second.CustomVisual, logger);

        // Assert
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(typeof(WarnedVisualObject).FullName!, entry.Message);
    }
}
