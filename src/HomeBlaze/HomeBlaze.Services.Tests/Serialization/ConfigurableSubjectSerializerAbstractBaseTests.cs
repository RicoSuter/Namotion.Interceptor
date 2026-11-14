using HomeBlaze.Abstractions;
using HomeBlaze.Abstractions.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using Namotion.Interceptor.Attributes;

namespace HomeBlaze.Services.Tests.Serialization;

public class ConfigurableSubjectSerializerAbstractBaseTests
{
    private readonly ConfigurableSubjectSerializer _serializer;

    public ConfigurableSubjectSerializerAbstractBaseTests()
    {
        var typeProvider = new TypeProvider();
        typeProvider.AddTypes([typeof(ShapeContainer), typeof(SquareShape), typeof(CircleShape)]);
        _serializer = new ConfigurableSubjectSerializer(typeProvider, new ServiceCollection().BuildServiceProvider());
    }

    [Fact]
    public void WhenDictionaryOfAbstractClassIsSerialized_ThenEachEntryCarriesItsType()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        var container = new ShapeContainer(context)
        {
            Shapes = new Dictionary<string, AbstractShape>
            {
                ["square"] = new SquareShape { Size = 2, Color = "red" },
                ["circle"] = new CircleShape { Size = 3 }
            }
        };

        // Act
        var json = _serializer.Serialize(container);

        // Assert
        Assert.Contains($"\"$type\": \"{typeof(SquareShape).FullName}\"", json);
        Assert.Contains($"\"$type\": \"{typeof(CircleShape).FullName}\"", json);
        Assert.Contains("\"red\"", json);
    }

    [Fact]
    public void WhenDictionaryOfAbstractClassIsDeserialized_ThenEachEntryHasItsConcreteType()
    {
        // Arrange
        var json = $$"""
            {
              "$type": "{{typeof(ShapeContainer).FullName}}",
              "shapes": {
                "square": { "$type": "{{typeof(SquareShape).FullName}}", "size": 2, "color": "red" },
                "circle": { "$type": "{{typeof(CircleShape).FullName}}", "size": 3 }
              }
            }
            """;

        // Act
        var container = Assert.IsType<ShapeContainer>(_serializer.Deserialize(json));

        // Assert
        var square = Assert.IsType<SquareShape>(container.Shapes["square"]);
        Assert.Equal(2, square.Size);
        Assert.Equal("red", square.Color);
        Assert.Equal(3, Assert.IsType<CircleShape>(container.Shapes["circle"]).Size);
    }
}

[InterceptorSubject]
public abstract partial class AbstractShape : IConfigurable
{
    [Configuration]
    public partial int Size { get; set; }

    protected AbstractShape()
    {
        Size = 0;
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

[InterceptorSubject]
public partial class SquareShape : AbstractShape
{
    [Configuration]
    public partial string Color { get; set; }

    public SquareShape()
    {
        Color = string.Empty;
    }
}

[InterceptorSubject]
public partial class CircleShape : AbstractShape
{
    public CircleShape()
    {
    }
}

[InterceptorSubject]
public partial class ShapeContainer : IConfigurable
{
    [Configuration]
    public partial Dictionary<string, AbstractShape> Shapes { get; set; }

    public ShapeContainer()
    {
        Shapes = new Dictionary<string, AbstractShape>();
    }

    public Task ApplyConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
