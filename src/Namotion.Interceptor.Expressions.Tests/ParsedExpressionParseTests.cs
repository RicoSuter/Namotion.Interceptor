using System.Collections.Immutable;
using Namotion.Interceptor.Expressions;

namespace Namotion.Interceptor.Expressions.Tests;

public class ParsedExpressionParseTests
{
    private static readonly ImmutableArray<string> ClockAndEventVariables = ["subject", "now", "time", "event"];

    private static ParsedExpression Parse(string text)
    {
        Assert.True(ParsedExpression.TryParse(text, '/', out var expression, out var error), error);
        return expression;
    }

    [Theory]
    [InlineData("subject.IsOn", "subject", "IsOn")]
    [InlineData("subject.Device.IsOn", "subject", "Device.IsOn")]
    [InlineData("subject", "subject", null)]
    [InlineData("subject[0]", "subject", "[0]")]
    public void WhenSeparatorIsADot_ThenAVariableEndsAtTheDot(string text, string expectedVariable, string? expectedMemberPath)
    {
        // Act
        Assert.True(ParsedExpression.TryParse(text, '.', out var expression, out var error), error);
        var context = new RecordingContext();
        expression.Evaluate(ref context);

        // Assert
        Assert.Equal(expectedVariable, context.Variable);
        Assert.Equal(expectedMemberPath, context.MemberPath);
    }

    [Theory]
    [InlineData("Temperature > Limits.Maximum", '.', true)]
    [InlineData("Temperature > Limits.Maximum", '/', false)]
    [InlineData("Temperature > Limits/Maximum", '/', true)]
    [InlineData("x == subject", '.', true)]
    [InlineData("x == subjects.Power", '.', true)]
    public void WhenSeparatorIsGiven_ThenAWordAfterAnOperatorWithTheSeparatorIsAPath(string text, char separator, bool isPath)
    {
        // Act
        Assert.True(ParsedExpression.TryParse(text, separator, out var expression, out var error), error);

        // Assert
        Assert.Equal(isPath ? 2 : 1, expression.Paths.Length);
    }

    [Fact]
    public void WhenSeparatorIsADotAndNumberHasADecimalPoint_ThenItIsANumberAndNoPath()
    {
        // Arrange
        Assert.True(ParsedExpression.TryParse("Temperature > 1.5", '.', out var expression, out var error), error);
        var context = new TestExpressionContext(new Dictionary<string, object?> { ["Temperature"] = 2.0 });

        // Act
        var result = expression.EvaluateCondition(ref context);

        // Assert
        Assert.Equal(["Temperature"], expression.Paths.ToArray());
        Assert.True(result);
    }

    [Theory]
    [InlineData('/')]
    [InlineData('.')]
    [InlineData(':')]
    [InlineData('\\')]
    public void WhenSeparatorIsValid_ThenItIsEchoedAndSeparatesThePath(char separator)
    {
        // Act
        Assert.True(ParsedExpression.TryParse($"a{separator}b == 1", separator, out var expression, out var error), error);

        // Assert
        Assert.Equal(separator, expression.PathSeparator);
        Assert.Equal([$"a{separator}b"], expression.Paths.ToArray());
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('a')]
    [InlineData('7')]
    [InlineData('\'')]
    [InlineData('"')]
    [InlineData('[')]
    [InlineData(']')]
    [InlineData('(')]
    [InlineData(')')]
    [InlineData('{')]
    [InlineData('}')]
    [InlineData('|')]
    [InlineData('&')]
    [InlineData('=')]
    [InlineData('!')]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData('+')]
    [InlineData('-')]
    [InlineData('*')]
    public void WhenSeparatorCannotBeUsed_ThenParsingThrows(char separator)
    {
        // Act & Assert
        var parseException = Assert.Throws<ArgumentException>(() => ParsedExpression.TryParse("a", separator, out _, out _));
        Assert.Equal("pathSeparator", parseException.ParamName);
        var templateException = Assert.Throws<ArgumentException>(() => TextTemplate.TryParse("a {{ b }}", separator, out _, out _));
        Assert.Equal("pathSeparator", templateException.ParamName);
    }

    [Theory]
    [InlineData("subject.Power", '.', "subject")]
    [InlineData("value", '.', "value")]
    [InlineData("subject/Power", '.', null)]
    [InlineData("subjects.Power", '.', null)]
    public void WhenPathIsChecked_ThenItsFirstSegmentDecidesTheVariable(string path, char separator, string? expected)
    {
        // Act
        ExpressionVariables.TryGetVariable(path, separator, out var variable);

        // Assert
        Assert.Equal(expected, variable);
    }

    [Theory]
    [InlineData("subject.Power", '.', "subject", "Power")]
    [InlineData("value.Device.IsOn", '.', "value", "Device.IsOn")]
    [InlineData("subject/Device/IsOn", '/', "subject", "Device/IsOn")]
    [InlineData("subject", '.', "subject", null)]
    [InlineData("subject[0]", '.', "subject", "[0]")]
    [InlineData("subject[0].Power", '.', "subject", "[0].Power")]
    [InlineData("subjects.Power", '.', null, null)]
    [InlineData("subject/Power", '.', null, null)]
    public void WhenPathIsSplit_ThenMemberPathFollowsTheVariableAndItsSeparator(
        string path, char separator, string? expectedVariable, string? expectedMemberPath)
    {
        // Act
        var isVariable = ExpressionVariables.TryGetVariable(path, separator, out var variable, out var memberPath);

        // Assert
        Assert.Equal(expectedVariable is not null, isVariable);
        Assert.Equal(expectedVariable, variable);
        Assert.Equal(expectedMemberPath, memberPath);
    }

    [Theory]
    [InlineData("motor/Speed", "motor/Speed")]
    [InlineData("  /Demo/Conveyor/CurrentSpeed  ", "/Demo/Conveyor/CurrentSpeed")]
    [InlineData("../notes-v2.md/motorstate", "../notes-v2.md/motorstate")]
    [InlineData("/Devices[a|b c]/Speed", "/Devices[a|b c]/Speed")]
    [InlineData("Vendor:Metadata", "Vendor:Metadata")]
    [InlineData("2024/Report.md/Title", "2024/Report.md/Title")]
    public void WhenTextIsOnePath_ThenSinglePathIsThatPath(string text, string path)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Equal(path, expression.SinglePath);
        Assert.Equal([path], expression.Paths.ToArray());
        Assert.Empty(expression.Pipes);
    }

    [Fact]
    public void WhenExpressionHasPipes_ThenTheyAreReadInOrderWithTheirArguments()
    {
        // Act
        var expression = Parse("meter/Power | unit:kW | default:\"off\":2 |raw");

        // Assert
        Assert.Equal("meter/Power", expression.SinglePath);
        Assert.Equal(3, expression.Pipes.Length);
        Assert.Equal("unit", expression.Pipes[0].Name);
        Assert.Equal(["kW"], expression.Pipes[0].Arguments.ToArray());
        Assert.Equal("default", expression.Pipes[1].Name);
        Assert.Equal(["off", "2"], expression.Pipes[1].Arguments.ToArray());
        Assert.Equal("raw", expression.Pipes[2].Name);
        Assert.Empty(expression.Pipes[2].Arguments);
    }

    [Fact]
    public void WhenExpressionReadsSeveralPaths_ThenEachIsListedOnceInOrder()
    {
        // Act
        var expression = Parse("IsOn == true && (Temperature > 1 || IsOn == false) || not subject/IsCharging and Temperature < subject/Maximum");

        // Assert
        Assert.Null(expression.SinglePath);
        Assert.Equal(["IsOn", "Temperature", "subject/IsCharging", "subject/Maximum"], expression.Paths.ToArray());
    }

    [Theory]
    [InlineData("Name == Kitchen")]
    [InlineData("Day == monday")]
    [InlineData("Name == 'Kitchen'")]
    [InlineData("Name == \"Kitchen\"")]
    public void WhenBareWordFollowsAnOperator_ThenItIsTextAndNoPath(string text)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Single(expression.Paths);
    }

    [Theory]
    [InlineData("value == subject")]
    [InlineData("Power > subject/Maximum")]
    [InlineData("Power > ./Maximum")]
    public void WhenRightOperandIsAVariableOrHasASlash_ThenItIsAPath(string text)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Equal(2, expression.Paths.Length);
    }

    [Theory]
    [InlineData("", "expected a value at position 0")]
    [InlineData("Temperature >", "expected a value after '>' at position 13")]
    [InlineData("Temperature = 1", "use '==' for equality at position 12")]
    [InlineData("(Temperature > 1", "expected ')' at position 16")]
    [InlineData("Name == \"abc", "unterminated string at position 8")]
    [InlineData("Temperature > 1 Name == x", "unexpected 'N' at position 16")]
    [InlineData("Temperature > 1.2.3", "invalid number '1.2.3' at position 14")]
    [InlineData("a and", "expected a value at position 5")]
    [InlineData("a == and", "expected a value after '==' at position 5")]
    [InlineData("a |", "expected a pipe name after '|' at position 3")]
    [InlineData("a | unit:", "expected an argument after ':' at position 9")]
    [InlineData("Interval > 30m1h", "invalid duration '30m1h' at position 11")]
    [InlineData("Interval > 1h1h", "invalid duration '1h1h' at position 11")]
    [InlineData("Interval > 5x", "invalid duration '5x' at position 11")]
    [InlineData("Interval > 1e5m", "invalid duration '1e5m' at position 11")]
    [InlineData("Interval > 5M", "invalid duration '5M' at position 11")]
    [InlineData("Interval > 1.5.5h", "invalid duration '1.5.5h' at position 11")]
    [InlineData("Interval > 10675200d", "duration out of range '10675200d' at position 11")]
    [InlineData("Interval > 10675199d2h48m5.4775808s", "duration out of range '10675199d2h48m5.4775808s' at position 11")]
    [InlineData("Interval > 99999999999999999999999999d", "duration out of range '99999999999999999999999999d' at position 11")]
    [InlineData("Interval > -99999999999999999999999999d9999999999999999999999999h", "duration out of range '-99999999999999999999999999d9999999999999999999999999h' at position 11")]
    [InlineData("Interval > 999999999999999999999999999999d", "duration out of range '999999999999999999999999999999d' at position 11")]
    [InlineData("Interval > 1h999999999999999999999999999999.5s", "duration out of range '1h999999999999999999999999999999.5s' at position 11")]
    [InlineData("Interval > 999999999999999999999999999999x", "invalid duration '999999999999999999999999999999x' at position 11")]
    [InlineData("Interval > 999999999999999999999999999999.5.5s", "invalid duration '999999999999999999999999999999.5.5s' at position 11")]
    [InlineData("x < 1e999", "number out of range '1e999' at position 4")]
    [InlineData("x < -1e999", "number out of range '-1e999' at position 4")]
    [InlineData("a- b", "put a space before '-' at position 1")]
    [InlineData("a-(b)", "put a space before '-' at position 1")]
    [InlineData("a*(b)", "put a space before '*' at position 1")]
    [InlineData("x > 5- 3", "put a space before '-' at position 5")]
    [InlineData("(a -)", "expected a value after '-' at position 4")]
    [InlineData("a -)", "expected a value after '-' at position 3")]
    [InlineData("a -| raw", "expected a value after '-' at position 3")]
    [InlineData("a -", "expected a value after '-' at position 3")]
    [InlineData("a * ", "expected a value after '*' at position 4")]
    [InlineData("x > -", "expected a value after '-' at position 5")]
    [InlineData("a -x", "put a space after '-' at position 2")]
    [InlineData("a -5", "put a space after '-' at position 2")]
    [InlineData("a +b", "put a space after '+' at position 2")]
    [InlineData("Now -5m", "put a space after '-' at position 4")]
    public void WhenExpressionIsInvalid_ThenErrorHasMessageAndPosition(string text, string expectedError)
    {
        // Act
        var success = ParsedExpression.TryParse(text, '/', out var expression, out var error);

        // Assert
        Assert.False(success);
        Assert.Null(expression);
        Assert.Equal(expectedError, error);
    }

    [Theory]
    [InlineData("Power > 5/2", '.', "invalid number '5/2' at position 8")]
    [InlineData("Power > 1.5.2", '.', "invalid number '1.5.2' at position 8")]
    [InlineData("Power > 5/2", ':', "invalid number '5/2' at position 8")]
    [InlineData("Power > 1.5", '.', null)]
    [InlineData("Power > 1.5", '/', null)]
    [InlineData("Power > 1.5", ':', null)]
    public void WhenTokenStartsLikeANumber_ThenItIsANumberUnlessItHoldsTheSeparator(string text, char separator, string? expectedError)
    {
        // Act
        var success = ParsedExpression.TryParse(text, separator, out var expression, out var error);

        // Assert
        Assert.Equal(expectedError is null, success);
        Assert.Equal(expectedError, error);
        if (expression is not null)
        {
            Assert.Equal(["Power"], expression.Paths.ToArray());
        }
    }

    [Theory]
    [InlineData("Power > 5/2", '/')]
    [InlineData("Power > 5:2", ':')]
    public void WhenTokenStartsLikeANumberAndHoldsTheSeparator_ThenItIsAPath(string text, char separator)
    {
        // Act
        var success = ParsedExpression.TryParse(text, separator, out var expression, out var error);

        // Assert
        Assert.True(success, error);
        Assert.Equal(2, expression!.Paths.Length);
    }

    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void WhenExpressionIsAtOrOverTheLengthLimit_ThenOnlyTheLongerOneFails(int length, bool expectedSuccess)
    {
        // Arrange
        var text = "a == '" + new string('x', length - 7) + "'";

        // Act
        var success = ParsedExpression.TryParse(text, '/', out _, out var error);

        // Assert
        Assert.Equal(length, text.Length);
        Assert.Equal(expectedSuccess, success);
        Assert.Equal(expectedSuccess ? null : "expression is longer than 1000 characters at position 1000", error);
    }

    [Fact]
    public void WhenExpressionIsLongerThanLimit_ThenReturnsLengthError()
    {
        // Arrange
        var text = "a == " + new string('1', 1000);

        // Act
        var success = ParsedExpression.TryParse(text, '/', out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is longer than 1000 characters at position 1000", error);
    }

    [Fact]
    public void WhenNestingExceedsLimit_ThenReturnsNestingError()
    {
        // Arrange
        var text = new string('(', 33) + "a" + new string(')', 33);

        // Act
        var success = ParsedExpression.TryParse(text, '/', out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is nested too deeply at position 32", error);
    }

    [Theory]
    [InlineData('!')]
    [InlineData('-')]
    public void WhenNotOrMinusIsRepeatedBeyondLimit_ThenReturnsNestingError(char prefix)
    {
        // Arrange
        var text = new string(prefix, 40) + "a";

        // Act
        var success = ParsedExpression.TryParse(text, '/', out _, out var error);

        // Assert
        Assert.False(success);
        Assert.Equal("expression is nested too deeply at position 32", error);
    }

    [Theory]
    [InlineData("not IsCharging")]
    [InlineData("!IsCharging")]
    [InlineData("NOT(IsCharging)")]
    [InlineData("a != b")]
    [InlineData("a>=1 AND b<=2 Or c")]
    [InlineData("note/Text")]
    [InlineData("-5 < Temperature")]
    [InlineData("Temperature > .5")]
    [InlineData("a == null")]
    [InlineData("a == TRUE")]
    public void WhenExpressionIsValid_ThenItParses(string text)
    {
        // Act & Assert
        Parse(text);
    }

    [Theory]
    // A duration is a literal
    [InlineData("Interval > 1h30m", new[] { "Interval" })]
    [InlineData("Interval > -5m", new[] { "Interval" })]
    [InlineData("Interval > 1.5d", new[] { "Interval" })]
    // Operands of arithmetic are values, so bare words there are paths
    [InlineData("a - b", new[] { "a", "b" })]
    [InlineData("a + b * c / d", new[] { "a", "b", "c", "d" })]
    [InlineData("(a + b) * 2 > c", new[] { "a", "b" })]
    [InlineData("x > Phase1 + Phase2", new[] { "x", "Phase1", "Phase2" })]
    [InlineData("x > Phase1 * 2", new[] { "x", "Phase1" })]
    [InlineData("x > Kitchen", new[] { "x" })]
    [InlineData("notes-v2.md == a-b", new[] { "notes-v2.md" })]
    [InlineData("a - -5 > 0", new[] { "a" })]
    // A minus is the sign of a number or a negation
    [InlineData("-x > 0", new[] { "x" })]
    [InlineData("x > -Limit", new[] { "x", "Limit" })]
    [InlineData("a * -x", new[] { "a", "x" })]
    [InlineData("-(a + b) > 0", new[] { "a", "b" })]
    [InlineData("a == - b", new[] { "a", "b" })]
    [InlineData("a - -x", new[] { "a", "x" })]
    [InlineData("Interval > - 5m", new[] { "Interval" })]
    [InlineData("/Room-1/Power > -/Room-1/Limit", new[] { "/Room-1/Power", "/Room-1/Limit" })]
    [InlineData("Temperature > -5", new[] { "Temperature" })]
    [InlineData("Power > -5/2", new[] { "Power", "-5/2" })]
    public void WhenExpressionHasDurationsArithmeticOrSigns_ThenItsPathsAreTheOperandsThatAreNoLiterals(string text, string[] expectedPaths)
    {
        // Act
        var expression = Parse(text);

        // Assert
        Assert.Equal(expectedPaths, expression.Paths.ToArray());
        Assert.Null(expression.SinglePath);
    }

    [Theory]
    [InlineData("now/Ticks > 1", false)]
    [InlineData("event/State == 1", false)]
    [InlineData("time", false)]
    // A declared variable matches only in its case
    [InlineData("Now", true)]
    [InlineData("NOW/Ticks", true)]
    [InlineData("Event/State", true)]
    public void WhenNameIsNoDeclaredVariable_ThenThePathIsResolvedAsAPath(string text, bool declaresClockAndEvent)
    {
        // Act
        var isParsed = declaresClockAndEvent
            ? ParsedExpression.TryParse(text, '/', ClockAndEventVariables, out var expression, out var error)
            : ParsedExpression.TryParse(text, '/', out expression, out error);
        var context = new RecordingContext();
        expression!.Evaluate(ref context);

        // Assert
        Assert.True(isParsed, error);
        Assert.Null(context.Variable);
        Assert.False(expression.TryGetVariable(expression.Paths[0], out _, out _));
        string[] expectedVariables = declaresClockAndEvent ? [.. ClockAndEventVariables] : ["value", "subject"];
        Assert.Equal(expectedVariables, expression.Variables.ToArray());
    }

    [Theory]
    [InlineData("now/Ticks > 1", "now", "Ticks")]
    [InlineData("event/State == 1", "event", "State")]
    [InlineData("time", "time", null)]
    [InlineData("subject[0]/Power", "subject", "[0]/Power")]
    public void WhenNameIsADeclaredVariable_ThenThePathIsResolvedAsThatVariable(string text, string expectedVariable, string? expectedMemberPath)
    {
        // Act
        Assert.True(ParsedExpression.TryParse(text, '/', ClockAndEventVariables, out var expression, out var error), error);
        var context = new RecordingContext();
        expression.Evaluate(ref context);
        var isVariable = expression.TryGetVariable(expression.Paths[0], out var variable, out var memberPath);

        // Assert
        Assert.Equal(expectedVariable, context.Variable);
        Assert.Equal(expectedMemberPath, context.MemberPath);
        Assert.True(isVariable);
        Assert.Equal(expectedVariable, variable);
        Assert.Equal(expectedMemberPath, memberPath);
        Assert.Equal(ClockAndEventVariables, expression.Variables);
    }

    [Theory]
    [InlineData("Since < now", false, new[] { "Since" })]
    [InlineData("Since < now", true, new[] { "Since", "now" })]
    [InlineData("State == event", false, new[] { "State" })]
    [InlineData("State == event", true, new[] { "State", "event" })]
    [InlineData("Mode == value", false, new[] { "Mode", "value" })]
    [InlineData("Mode == value", true, new[] { "Mode" })]
    public void WhenBareWordFollowsAComparison_ThenItIsAPathOnlyWhenItIsADeclaredVariable(string text, bool declaresClockAndEvent, string[] expectedPaths)
    {
        // Act
        var isParsed = declaresClockAndEvent
            ? ParsedExpression.TryParse(text, '/', ClockAndEventVariables, out var expression, out var error)
            : ParsedExpression.TryParse(text, '/', out expression, out error);

        // Assert
        Assert.True(isParsed, error);
        Assert.Equal(expectedPaths, expression!.Paths.ToArray());
    }

    [Fact]
    public void WhenTemplateDeclaresVariables_ThenItsExpressionsHaveThem()
    {
        // Act
        Assert.True(TextTemplate.TryParse("At {{ now }} for {{ event/State }}", '/', ClockAndEventVariables, out var template, out var error), error);

        // Assert
        Assert.All(template.Expressions, expression => Assert.Equal(ClockAndEventVariables, expression.Variables));
        Assert.True(template.Expressions[1].TryGetVariable("event/State", out var variable, out var memberPath));
        Assert.Equal("event", variable);
        Assert.Equal("State", memberPath);
    }

    [Theory]
    [InlineData('/', new[] { "" })]
    [InlineData('/', new[] { "1st" })]
    [InlineData('/', new[] { "_now" })]
    [InlineData('/', new[] { "now/x" })]
    [InlineData('/', new[] { "time zone" })]
    [InlineData('/', new[] { "and" })]
    [InlineData('/', new[] { "NULL" })]
    [InlineData('_', new[] { "now_utc" })]
    [InlineData('/', new[] { "now", "time", "now" })]
    public void WhenVariableIsNoWordHoldsTheSeparatorOrIsDeclaredTwice_ThenParsingThrows(char separator, string[] variables)
    {
        // Act & Assert
        var parseException = Assert.Throws<ArgumentException>(() => ParsedExpression.TryParse("a", separator, [.. variables], out _, out _));
        Assert.Equal("variables", parseException.ParamName);
        var templateException = Assert.Throws<ArgumentException>(() => TextTemplate.TryParse("a {{ b }}", separator, [.. variables], out _, out _));
        Assert.Equal("variables", templateException.ParamName);
    }

    [Fact]
    public void WhenVariablesAreNotInitialized_ThenParsingThrows()
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentException>(() => ParsedExpression.TryParse("a", '/', default(ImmutableArray<string>), out _, out _));
        Assert.Equal("variables", exception.ParamName);
    }

    private struct RecordingContext : IExpressionContext
    {
        public string? Variable;
        public string? MemberPath;

        public ExpressionValue ResolvePath(string path) => ExpressionValue.Missing;

        public ExpressionValue ResolveVariable(string variable, string? memberPath)
        {
            Variable = variable;
            MemberPath = memberPath;
            return ExpressionValue.Missing;
        }

        public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

        public string ToText(ExpressionValue result) => string.Empty;
    }
}
