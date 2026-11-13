using HomeBlaze.Virtual.Tests.Testing;

namespace HomeBlaze.Virtual.Tests;

public class VirtualExpressionTests
{
    [Fact]
    public void WhenAnExpressionHasAPipe_ThenItIsRejected()
    {
        // Act
        var isParsed = VirtualExpression.TryParse("Power | raw", out _, out var error);

        // Assert
        Assert.False(isParsed);
        Assert.Equal("Pipes are not supported in a virtual property.", error);
    }

    [Fact]
    public void WhenAnAbsolutePathIsResolved_ThenItLeadsFromTheRoot()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 5 });

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, "/Meter/Power", graph.Root);

        // Assert
        Assert.Equal(5m, property?.GetValue());
    }

    [Fact]
    public void WhenAPathWithoutSeparatorIsResolved_ThenItIsAPropertyOfTheScope()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 7 };
        graph.Add("Meter", meter);

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, "Power", meter);

        // Assert
        Assert.Equal(7m, property?.GetValue());
    }

    [Theory]
    [InlineData("./Power")]
    [InlineData("../Meter/Power")]
    public void WhenARelativePathWithAPrefixIsResolved_ThenItLeadsFromTheScope(string path)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter { Power = 9 };
        graph.Add("Meter", meter);

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, path, meter);

        // Assert
        Assert.Equal(9m, property?.GetValue());
    }

    [Fact]
    public void WhenASecretIsResolved_ThenNoPropertyIsFound()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { ApiKey = "secret" });

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, "/Meter/ApiKey", graph.Root);

        // Assert
        Assert.Null(property);
    }

    [Fact]
    public void WhenThePropertyOfAPathIsMissing_ThenTheMissIsRecordedWithItsSubject()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        var meter = new TestMeter();
        graph.Add("Meter", meter);
        List<VirtualMiss>? misses = null;

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, "/Meter/Wind", graph.Root, ref misses);

        // Assert
        Assert.Null(property);
        var miss = Assert.Single(misses!);
        Assert.Same(meter, miss.Subject);
        Assert.Equal("Wind", miss.Name);
    }

    [Fact]
    public void WhenAnExpressionIsEvaluated_ThenItComputesWithTheValuesOfItsPaths()
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 21 });
        Assert.True(VirtualExpression.TryParse("/Meter/Power * 2", out var expression, out _));

        // Act
        var value = VirtualExpression.Evaluate(expression, graph.Resolver, graph.Root);

        // Assert
        Assert.Equal(42m, value.Value);
    }

    [Fact]
    public void WhenAPathIsMalformed_ThenNoPropertyIsFound()
    {
        // Arrange
        var graph = new VirtualTestGraph();

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, "/Meter[/Power", graph.Root);

        // Assert
        Assert.Null(property);
    }
}
