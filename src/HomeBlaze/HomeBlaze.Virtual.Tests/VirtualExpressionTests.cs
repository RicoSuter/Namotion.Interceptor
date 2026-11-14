using HomeBlaze.Virtual.Tests.Testing;
using Namotion.Interceptor.Registry;

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

    [Theory]
    [InlineData("Power")]
    [InlineData("./Power")]
    [InlineData("../Meter/Power")]
    public void WhenARelativePathIsResolved_ThenItLeadsFromTheScope(string path)
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

    [Theory]
    [InlineData("/Meter/ApiKey")]
    [InlineData("/Meter[/Power")]
    public void WhenAPathIsASecretOrMalformed_ThenNoPropertyIsFound(string path)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 1, ApiKey = "secret" });

        // Act
        var property = VirtualExpression.ResolveProperty(graph.Resolver, path, graph.Root);

        // Assert
        Assert.Null(property);
    }

    [Theory]
    [InlineData("/Meter/Power")]
    [InlineData("//Meter/Power")]
    public void WhenASubjectOnAnAbsolutePathIsReplaced_ThenADerivedPropertyThatResolvesThePathIsRecalculated(string path)
    {
        // Arrange
        var graph = new VirtualTestGraph();
        graph.Add("Meter", new TestMeter { Power = 4 });
        var registered = graph.Root.TryGetRegisteredSubject()!;

        // The second property resolves the path after the first one, so the resolver's cache of absolute paths has it.
        registered.AddDerivedProperty<decimal?>("First", subject =>
            (decimal?)VirtualExpression.ResolveProperty(graph.Resolver, path, subject)?.GetValue());
        var evaluations = 0;
        registered.AddDerivedProperty<decimal?>("Second", subject =>
        {
            evaluations++;
            return (decimal?)VirtualExpression.ResolveProperty(graph.Resolver, path, subject)?.GetValue();
        });
        var evaluationsBefore = evaluations;

        // Act
        graph.Add("Meter", new TestMeter { Power = 8 });

        // Assert
        Assert.True(evaluations > evaluationsBefore);
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
}
