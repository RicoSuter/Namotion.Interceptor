using System.Linq.Expressions;
using Namotion.Interceptor.Connectors.Mapping;

namespace Namotion.Interceptor.Connectors.Tests.Mapping;

public class ExpressionPathHelperTests
{
    private class TestSubject
    {
        public string Name { get; set; } = "";
        public double Value { get; set; }
        public TestChild Child { get; set; } = new();
        public TestChild[] Items { get; set; } = [new()];

        public TestChild GetChild() => Child;
    }

    private class TestChild
    {
        public int Temperature { get; set; }
        public TestGrandChild Inner { get; set; } = new();
    }

    private class TestGrandChild
    {
        public bool Active { get; set; }
    }

    [Fact]
    public void WhenSingleProperty_ThenReturnsPropertyName()
    {
        // Arrange
        Expression<Func<TestSubject, string>> expression = s => s.Name;

        // Act
        var path = ExpressionPathHelper.GetPathFromExpression(expression.Body);

        // Assert
        Assert.Equal("Name", path);
    }

    [Fact]
    public void WhenNestedProperty_ThenReturnsDottedPath()
    {
        // Arrange
        Expression<Func<TestSubject, int>> expression = s => s.Child.Temperature;

        // Act
        var path = ExpressionPathHelper.GetPathFromExpression(expression.Body);

        // Assert
        Assert.Equal("Child.Temperature", path);
    }

    [Fact]
    public void WhenDeeplyNestedProperty_ThenReturnsDottedPath()
    {
        // Arrange
        Expression<Func<TestSubject, bool>> expression = s => s.Child.Inner.Active;

        // Act
        var path = ExpressionPathHelper.GetPathFromExpression(expression.Body);

        // Assert
        Assert.Equal("Child.Inner.Active", path);
    }

    [Fact]
    public void WhenConvertWrappedExpression_ThenUnwrapsAndReturnsPath()
    {
        // Arrange - simulates boxing of value type (e.g., Map<object>(s => s.Value, ...))
        Expression<Func<TestSubject, object>> expression = s => s.Value;

        // Act
        var path = ExpressionPathHelper.GetPathFromExpression(expression.Body);

        // Assert
        Assert.Equal("Value", path);
    }

    [Fact]
    public void WhenNonMemberExpression_ThenThrowsArgumentException()
    {
        // Arrange
        Expression<Func<TestSubject, int>> expression = s => 42;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
    }

    [Fact]
    public void WhenMethodCallExpression_ThenThrowsArgumentException()
    {
        // Arrange
        Expression<Func<TestSubject, string>> expression = s => s.Name.ToString();

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
    }

    [Fact]
    public void WhenMidChainMethodCall_ThenThrowsArgumentException()
    {
        // Arrange - a method call mid-chain cannot be represented as a dotted path
        Expression<Func<TestSubject, int>> expression = s => s.GetChild().Temperature;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
    }

    [Fact]
    public void WhenIndexerExpression_ThenThrowsArgumentException()
    {
        // Arrange - an indexer (array index) mid-chain cannot be represented as a dotted path
        Expression<Func<TestSubject, int>> expression = s => s.Items[0].Temperature;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
    }

    [Fact]
    public void WhenCapturedVariableExpression_ThenThrowsArgumentException()
    {
        // Arrange - the chain is rooted at a captured variable, not the lambda parameter
        var captured = new TestSubject();
        Expression<Func<TestSubject, string>> expression = s => captured.Name;

        // Act & Assert
        Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
    }

    [Fact]
    public void WhenIdentityExpression_ThenThrowsWithClearMessage()
    {
        // Arrange - the expression selects the parameter itself, not a property
        Expression<Func<TestSubject, TestSubject>> expression = s => s;

        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() =>
            ExpressionPathHelper.GetPathFromExpression(expression.Body));
        Assert.Contains("the lambda parameter itself", exception.Message);
    }
}
