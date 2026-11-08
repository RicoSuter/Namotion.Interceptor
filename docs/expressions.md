# Expressions

The `Namotion.Interceptor.Expressions` package is a small language to select, compare and format property values. An expression is parsed once into an immutable tree and evaluated many times against a struct context that the host implements, without reflection, so it suits templates and filters that run for every subject or every change.

```xml
<PackageReference Include="Namotion.Interceptor.Expressions" />
```

```text
Temperature > 20 and not (Mode == Away)
Charging {{ subject/ChargingPower }} kW
```

The paths of these samples use `/` as the separator.

The package has no opinion on where values come from. The host resolves paths and variables, applies pipes and turns results into text; the language decides how to parse an expression, how to combine and compare values, and what is unknown. The [MCP server](mcp.md#filtering-by-value) is one host: its `search` tool filters subjects with it.

## Grammar

```
expression := or ( '|' pipe )*              pipes bind loosest and apply left to right
pipe       := name ( ':' argument )*        an argument is a literal or a name
or         := and ( ( 'or' | '||' ) and )*
and        := not ( ( 'and' | '&&' ) not )*
not        := ( 'not' | '!' ) not | comparison
comparison := sum ( ( '==' | '!=' | '<' | '<=' | '>' | '>=' ) sum )?
sum        := product ( ( '+' | '-' ) product )*
product    := negation ( ( '*' | '/' ) negation )*
negation   := '-' negation | operand            unless the '-' is the sign of a number, see the Arithmetic rule below
operand    := literal | path | '(' or ')'
literal    := number | duration | "text" | 'text' | true | false | null
```

- **Keywords** (`and`, `or`, `not`, `true`, `false`, `null`) are read without regard to case. `and`, `or` and `not` end at white space, an opening parenthesis or the end of the text, so a path such as `note/Text` is not `not`; `true`, `false` and `null` are whole tokens.
- **Equality** is `==`. A single `=` is an error with the hint `use '==' for equality`.
- **Both sides** of a comparison may be paths, such as `Power > MaximumPower`.
- **Bare operands**: an operand without a comparison is a condition. It is true only when its value is the boolean `true`. Any other value is false, and a missing value is unknown. So when `Device` holds a subject, `Device` is false and `not Device` is true; test for a missing value with `Device == null` instead.
- **Literals**: a number is a token that starts with a digit or with a dot followed by a digit, with or without the sign that the **Arithmetic** rule below describes, unless it holds the path separator: with `/` as separator `5/2` is a path, but with `:` it is an invalid number and `5:2` is a path. A number is read in the invariant culture. Without an exponent and with at most 28 digits, none beyond the 28th decimal place, it is a `decimal`, so `0.1` is exactly one tenth, and it has no trailing zeros, so `1.50` is written as `1.5`; with an exponent, such as `1e3`, or with more digits it is a `double`, and one beyond the range of `double`, such as `1e999`, is the error `number out of range`. When the separator is `.`, a dot is the decimal point, so `1.5` is a number and `1.5.2` is an invalid number. So a path cannot start with a digit when the separator is `.`, but its later segments can, as in `Device.2nd`. A text is in double or single quotes and has no escapes.
- **Durations**: a duration is a number followed by a unit, `d`, `h`, `m`, `s` or `ms`, or several such pairs from the largest unit to the smallest, each unit at most once, such as `90s`, `1h30m`, `1.5d` or `-5m`. Units are lower case. It is read as a `TimeSpan`, exact to the tick over its whole range. A token that starts like a number and ends with a letter is a duration, so `30m1h` and `5M` are the error `invalid duration`, and a duration beyond the range of `TimeSpan` is the error `duration out of range`.
- **A bare word after a comparison operator is text**, such as the name of an enum value in `Mode == Away`, unless it holds the path separator or an index (`Device.Mode`, `Devices[a].Mode`) or is a variable, see [Paths and variables](#paths-and-variables), or is an operand of arithmetic: in `Total > Phase1 + Phase2` both words after `>` are paths. Elsewhere, a word is a path.
- **A path token** ends at white space, a parenthesis, a quote, or one of `| & = ! < >`. Inside brackets everything up to the closing `]` belongs to the token, so a key may hold any of these characters.
- **Arithmetic**: `+`, `-`, `*` and `/` stand alone between operands, followed by white space, an opening parenthesis or the end of the text, as in `Power - 100` or `a * (b + c)`. `*` and `/` bind tighter than `+` and `-`, and all four bind tighter than comparisons. A `-` at the start of an operand negates it and binds tighter than `*` and `/`: `-Power`, `-(a + b)` and `- 5m`, and in `x > -Limit` the word `Limit` is a path. So a path cannot start with `-` unless the `-` stands before a digit, as in `-5/2` with `/` as separator. A sign directly before a digit, or before a dot followed by a digit, is part of the number, so `-5` and `-.5` are numbers and `a -5` is the error `put a space after '-'`; `a- b` is the error `put a space before '-'`, and these characters inside a path token are part of the path, as in `notes-v2.md`. See [Arithmetic](#arithmetic).
- **Limits**: an expression is at most 1000 characters long and nests at most 32 levels, counting parentheses, each `not` and each negation.
- **Errors**: `ParsedExpression.TryParse` returns false for a mistake in the text, with an error of the form `<message> at position <n>` and a zero-based position, such as `expected ')' at position 16`. It throws an `ArgumentException` for an invalid path separator, which is a mistake of the host.

## Paths and variables

A path uses the syntax of the host: its segments are divided by a separator character, and a segment may carry an index in brackets, such as `Devices[a].IsOn`. Indexes always use `[` and `]`, whatever the separator. The parser needs the separator to tell a bare word from a path, so every parse names it, and `ParsedExpression.PathSeparator` echoes it:

```csharp
ParsedExpression.TryParse(text, '.', out var expression, out var error);
```

The separator cannot be white space, a letter, a digit, a quote, a bracket or one of `| & = ! < > + - *`, and `TryParse` throws an `ArgumentException` for such a character. `ParsedExpression.Paths` lists the distinct paths of an expression in order of first appearance, and `SinglePath` is the path of an expression that is nothing but one path. How a path resolves is up to the host. When it comes from the [registry](registry.md#path-syntax), the host can resolve it with a path provider.

By default two names are variables with a meaning the host gives them:

| Variable | Means |
|----------|-------|
| `value` | The value the expression is evaluated for, such as the value of a property that is shown |
| `subject` | The subject of that value, such as the owner of the property |

A host that gives other names a meaning declares its variables when it parses, and they replace the default ones:

```csharp
ParsedExpression.TryParse(text, '/', ["subject", "now"], out var expression, out var error);
```

`TextTemplate.TryParse` takes them the same way. A variable is a word of letters, digits and underscores that starts with a letter, holds no path separator and is no keyword such as `and` or `null`, and is declared once; `TryParse` throws an `ArgumentException` for another name. A name is a variable only for the host that declares it: with the default variables, `now/Ticks` is a path whose first segment is named `now`, and a bare `now` after a comparison operator is text.

A path that starts with a variable, such as `subject/Power`, is resolved with `IExpressionContext.ResolveVariable`; every other path with `ResolvePath`. `ParsedExpression.TryGetVariable` splits the first segment off a path of the expression, with the rest as the member path, and `ExpressionVariables.TryGetVariable` does the same with the default variables. A host binds the variables it has a meaning for. An expression that uses a variable the host does not bind is the host's error to report: check `ParsedExpression.Paths` before evaluating and refuse the expression, as the MCP `where` filter does for `value`. At run time an unbound variable resolves to `ExpressionValue.Missing`.

## Truth and missing values

An expression is evaluated as a condition with three values: true, false and unknown. A value is missing when a path leads nowhere or the value is null. A comparison is unknown when a value it needs is missing, when it compares types that do not fit, or when a number is NaN.

- `not` of unknown is unknown.
- `and` and `or` follow Kleene logic: `false and unknown` is false, `true or unknown` is true, and every other mix with unknown is unknown.
- `== null` tests for a missing value and is never unknown: it is true when the value is missing, and `!= null` is false then. Any other operator with `null` is unknown.
- A condition matches only when it is true. So `Power > 5` matches no subject without a `Power`, and neither does `not (Power > 5)`.

`ParsedExpression.EvaluateCondition` gives `true`, `false` or `null` for unknown.

## Comparisons

Two values that are not null compare by these rules:

- **Numbers** of any numeric type compare numerically. Integers and decimals, number literals included, compare exactly, so a `long` of `9007199254740993` does not equal the literal `9007199254740992`; any other pair compares as `double`. A `float` compares with a `double` or a `decimal`, such as a literal, as a `float`, so a value of `0.1f` equals the literal `0.1`.
- **NaN** never compares: the comparison is unknown.
- **Enums** compare with a text by name without regard to case, for `==` and `!=` only. Two values of the same enum type compare by order with every operator.
- **Texts** compare without regard to case for `==` and `!=`, and by ordinal order for `<`, `<=`, `>` and `>=`.
- **Dates**: a `DateTime` or `DateTimeOffset` compares with a text literal in ISO 8601, and a `DateTime` and a `DateTimeOffset` compare by their instants. A text without an offset is read as UTC, and so is a `DateTime` without a kind. A local `DateTime` whose UTC falls outside the range of `DateTime`, such as `DateTime.MinValue` in a time zone ahead of UTC, has no instant, so its comparison is unknown.
- **Durations**: a `TimeSpan` compares with another `TimeSpan`, such as a duration literal: `Interval > 1m30s`.
- **Booleans** compare with booleans for `==` and `!=` only.
- **Every other pair** has no order, so the comparison is unknown.

## Arithmetic

Two values that are not null compute by these rules:

- **Numbers**: two integers or decimals compute as `decimal` when the result is exact, so `0.1 + 0.2` equals `0.3`, a `decimal` of `0.1m` times `3` equals `0.3`, and `3 / 4` is `0.75`. A `decimal` result has no trailing zeros, so a `decimal` of `1.50m` times `2` is written as `3`. A result that would round or overflow computes as `double`. A `float` with a `float` or a `decimal`, such as a number literal, computes as `float`, so `F + 0` compares with a literal as the `float` itself does, see [Comparisons](#comparisons); a `float` with a `double` or an integer computes as `double`, which keeps the precision of both. Every other pair, such as a `double` and a literal, computes as `double`, so a `double` of `0.1` plus `0.2` is `0.30000000000000004`. A division by zero, a NaN operand, or any other result that is no finite number, is missing, and a negative zero is zero.
- **Instants**: a `DateTimeOffset` or `DateTime` minus another one is a `TimeSpan`, and an instant plus or minus a `TimeSpan`, or a `TimeSpan` plus an instant, is an instant, so `StoppedAt - StartedAt > 15m` tests an elapsed time. There is no built-in clock: a host that offers the current time gives it as a property or a variable, such as `now` in `now - StartedAt > 15m`. Instant arithmetic gives a `DateTimeOffset`.
- **Durations**: two `TimeSpan` values add and subtract.
- **Negation**: `-` gives a number or a `TimeSpan` the other sign. An integer negates to a `decimal`, and a `float`, a `double` and a `decimal` keep their type.
- **Every other pair**, such as a text, a boolean, an instant plus an instant or a duration times a number, is missing, and so is a result beyond the range of its type.

A missing operand makes the result missing, like a path that leads nowhere. A computed value has no property, so a host shows it without the unit of a property. Each operator and negation boxes its result, so it allocates one small object per evaluation; comparisons, literals and conditions allocate nothing.

## Hosting an expression

A host implements `IExpressionContext` as a struct and passes it by reference, so an evaluation allocates no context. This sample resolves paths from a dictionary:

```csharp
using System.Collections.Immutable;
using Namotion.Interceptor.Expressions;

var values = new Dictionary<string, object?> { ["Temperature"] = 21.5, ["Mode"] = "Eco" };
var context = new DictionaryContext(values);

if (!ParsedExpression.TryParse("Temperature > 20 and not (Mode == Away)", '.', out var condition, out var error))
{
    Console.WriteLine(error);
    return;
}

Console.WriteLine(condition.EvaluateCondition(ref context)); // True

if (!TextTemplate.TryParse("It is {{ Temperature }} degrees in {{ Mode }} mode.", '.', out var template, out error))
{
    Console.WriteLine(error);
    return;
}

Console.WriteLine(template.Render(ref context)); // It is 21.5 degrees in Eco mode.

readonly struct DictionaryContext(IReadOnlyDictionary<string, object?> values) : IExpressionContext
{
    public ExpressionValue ResolvePath(string path) =>
        values.TryGetValue(path, out var value) ? ExpressionValue.FromValue(value) : ExpressionValue.Missing;

    public ExpressionValue ResolveVariable(string variable, string? memberPath) => ExpressionValue.Missing;

    public ExpressionValue ApplyPipe(string name, ImmutableArray<string> arguments, ExpressionValue input) => input;

    public string ToText(ExpressionValue result) => Convert.ToString(result.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
}
```

`ParsedExpression` offers three evaluations:

| Method | Gives |
|--------|-------|
| `EvaluateCondition` | `true`, `false` or `null` for unknown, without pipes |
| `Evaluate` | An `ExpressionValue` after the pipes were applied: a path gives its value, a condition gives a boolean or a missing value |
| `EvaluateWithoutPipes` | The value before the pipes |

`ExpressionValue` carries the `Value`, the registered `Property` it was read from, if any, so that a host can format it with the unit or the source of the property, and `IsText` for a value that is already the text to show. Create one with `FromProperty`, `FromValue`, `FromText` or `FromBoolean`.

A `TextTemplate` is a text with expressions between double braces. It is parsed once with `TextTemplate.TryParse` and written with `Render`, which asks the context for the text of each expression with `ToText`. A text without `{{` is plain text, braces cannot be escaped, and the paths of its expressions use the separator given to `TryParse`. An error is that of the first expression that does not parse, with a position that counts inside the braces of that expression, from its first character that is no white space, or `'{{' is not closed at position <n>`, which counts in the whole text.

## Pipes

A pipe after an expression, such as `Power | format:N1`, passes the result on to a function of the host. A pipe has a name and arguments divided by colons. An argument is a literal or a word, or a text in quotes, and is passed to the host without its quotes. Pipes apply left to right, and `|` is not `||`.

The language does not know any pipe. `ParsedExpression.Pipes` lists them, so a host can refuse an unknown one when it checks the expression, and `Evaluate` calls `IExpressionContext.ApplyPipe` for each of them. A host that evaluates conditions, such as a filter, rejects pipes, because a condition has no text to format.

## Related

- [Registry](registry.md#path-syntax) describes the paths that registry-based hosts resolve
- [MCP Server](mcp.md#filtering-by-value) uses the language to filter the subjects of `search` by their property values
