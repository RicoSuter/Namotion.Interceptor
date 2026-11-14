using System.Collections.Immutable;
using Namotion.Interceptor.Expressions;

namespace Namotion.Interceptor.Expressions.Tests;

public class TextTemplateTests
{
    private static string Render(string text, Dictionary<string, object?> values)
    {
        Assert.True(TextTemplate.TryParse(text, '/', out var template, out var error), error);
        var context = new TestExpressionContext(values);
        return template.Render(ref context);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("plain text", "plain text")]
    [InlineData("{{ Power }}", "7400")]
    [InlineData("Power: {{ Power }} W", "Power: 7400 W")]
    [InlineData("{{ Name }}{{ Power }}", "Kitchen7400")]
    [InlineData("{{ Name | upper }}!", "KITCHEN!")]
    [InlineData("{{ Power > 1000 }}", "True")]
    [InlineData("A } and a { stay", "A } and a { stay")]
    public void WhenTemplateIsRendered_ThenItsHolesAreReplacedByTheirText(string text, string expected)
    {
        // Arrange
        var values = new Dictionary<string, object?> { ["Power"] = 7400m, ["Name"] = "Kitchen" };

        // Act
        var result = Render(text, values);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void WhenTemplateIsParsed_ThenItListsItsExpressions()
    {
        // Act
        Assert.True(TextTemplate.TryParse("{{ a }} and {{ b/C > 1 }}", '/', out var template, out _));

        // Assert
        Assert.Equal(["a", "b/C > 1"], template.Expressions.Select(expression => expression.Text).ToArray());
    }

    [Theory]
    [InlineData("a {{ b", "'{{' is not closed at position 2")]
    [InlineData("{{ a = 1 }}", "use '==' for equality at position 2")]
    [InlineData("{{ }}", "expected a value at position 0")]
    public void WhenTemplateIsInvalid_ThenErrorSaysWhat(string text, string expectedError)
    {
        // Act
        var success = TextTemplate.TryParse(text, '/', out var template, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(template);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("value", "value|")]
    [InlineData("subject", "subject|")]
    [InlineData("subject/IsCharging", "subject|IsCharging")]
    [InlineData("subject/Session/Energy", "subject|Session/Energy")]
    [InlineData("subject[0]", "subject|[0]")]
    [InlineData("values/Power", "path:values/Power")]
    public void WhenPathStartsWithAVariable_ThenTheContextGetsTheVariableAndTheRest(string path, string expected)
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse(path, '/', out var expression, out _));
        var calls = new List<string>();
        var context = new RecordingContext(calls);

        // Act
        expression.Evaluate(ref context);

        // Assert
        Assert.Equal([expected], calls);
    }

    [Fact]
    public void WhenPlainTextIsRenderedAgain_ThenNoBytesAreAllocated()
    {
        // Arrange
        Assert.True(TextTemplate.TryParse("plain text", '/', out var template, out _));
        var context = new TestExpressionContext([]);
        template.Render(ref context);

        // Act
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            template.Render(ref context);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        Assert.Equal(0, allocated);
    }

    private readonly struct RecordingContext(List<string> calls) : IExpressionContext
    {
        public ExpressionValue ResolvePath(string path)
        {
            calls.Add($"path:{path}");
            return ExpressionValue.Missing;
        }

        public ExpressionValue ResolveVariable(string variable, string? memberPath)
        {
            calls.Add($"{variable}|{memberPath}");
            return ExpressionValue.Missing;
        }

        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
