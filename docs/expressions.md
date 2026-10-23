# Expressions

The `Namotion.Interceptor.Expressions` package is a small language to select, compare and format property values. An expression is parsed once into an immutable tree and evaluated many times against a struct context that the host implements, without reflection, so it suits templates and filters that run for every subject or every change.

```xml
<PackageReference Include="Namotion.Interceptor.Expressions" />
```

```text
Temperature > 20 and not (Mode == Away)
Charging {{ subject/ChargingPower }} kW
```

The paths of these samples are written with `/` as the separator.

The package has no opinion on where values come from. The host resolves paths and variables, applies pipes and turns results into text; the language decides how to parse an expression, how to combine and compare values, and what is unknown. The [MCP server](mcp.md#filtering-by-value) is one host: its `search` tool filters subjects with it.

## Grammar

```
expression := or ( '|' pipe )*              pipes bind loosest and apply left to right
pipe       := name ( ':' argument )*        an argument is a literal or a name
or         := and ( ( 'or' | '||' ) and )*
and        := not ( ( 'and' | '&&' ) not )*
not        := ( 'not' | '!' ) not | comparison
comparison := operand ( ( '==' | '!=' | '<' | '<=' | '>' | '>=' ) operand )?
operand    := literal | path | '(' or ')'
literal    := number | "text" | 'text' | true | false | null
```

- **Keywords** (`and`, `or`, `not`, `true`, `false`, `null`) are read without regard to case. `and`, `or` and `not` end at white space, an opening parenthesis or the end of the text, so a path such as `note/Text` is not `not`; `true`, `false` and `null` are whole tokens.
- **Equality** is `==`. A single `=` is an error with the hint `use '==' for equality`.
- **Both sides** of a comparison may be paths, such as `Power > MaximumPower`.
- **Bare operands**: an operand without a comparison is a condition. It is true only when its value is the boolean `true`. Any other value is false, and a missing value is unknown. So when `Device` holds a subject, `Device` is false and `not Device` is true; test for a missing value with `Device == null` instead.
- **Literals**: a number is a token that starts with a digit, or with a sign or a dot followed by a digit, and holds no `/`; it is read as `double` in the invariant culture. So when the separator is `.`, a word that starts with a digit is a number and a path segment cannot start with a digit. A text is in double or single quotes and has no escapes.
- **A bare word after an operator is text**, such as the name of an enum value in `Mode == Away`, unless it holds the path separator or an index (`Device.Mode`, `Devices[a].Mode`) or is a variable, see [Paths and variables](#paths-and-variables). Elsewhere, a word is a path.
- **A path token** ends at white space, a parenthesis, a quote, or one of `| & = ! < >`. Inside brackets everything up to the closing `]` belongs to the token, so a key may hold any of these characters.
- **Arithmetic is reserved**: `+`, `-`, `*` and `/` standing alone between operands, such as `a - b`, are rejected with `arithmetic is not supported`, so a later version can add arithmetic without changing what an expression means. A sign before a number is part of the number, and these characters inside a path token are part of the path.
- **Limits**: an expression is at most 1000 characters long and nests at most 32 levels, counting parentheses and each `not`.
- **Errors**: `ParsedExpression.TryParse` does not throw. It returns false and an error of the form `<message> at position <n>`, with a zero-based position, such as `expected ')' at position 16`.

## Paths and variables

A path is written in the syntax of the host: its segments are divided by a separator character, and a segment may carry an index in brackets, such as `Devices[a].IsOn`. Indexes are always written with `[` and `]`, whatever the separator. The parser needs the separator to tell a bare word from a path, so every parse names it, and `ParsedExpression.PathSeparator` echoes it:

```csharp
ParsedExpression.TryParse(text, '.', out var expression, out var error);
```

The separator cannot be white space, a letter, a digit, a quote, a bracket or one of `| & = ! < >`, and `TryParse` throws an `ArgumentException` for such a character. `ParsedExpression.Paths` lists the distinct paths of an expression in order of first appearance, and `SinglePath` is the path of an expression that is nothing but one path. How a path resolves is up to the host. When it comes from the [registry](registry.md#path-syntax), the host can resolve it with a path provider.

Two names are variables with a meaning the host gives them:

| Variable | Means |
|----------|-------|
| `value` | The value the expression is evaluated for, such as the value of a property that is shown |
| `subject` | The subject of that value, such as the owner of the property |

A path that starts with a variable, such as `subject/Power`, is resolved with `IExpressionContext.ResolveVariable`; every other path with `ResolvePath`. `ExpressionVariables.TryGetVariable` splits the first segment off a path, with the rest as the member path. A host binds the variables it has a meaning for. An expression that uses a variable the host does not bind is the host's error to report: check `ParsedExpression.Paths` before evaluating and refuse the expression, as the MCP `where` filter does for `value`. At run time an unbound variable resolves to `ExpressionValue.Missing`.

## Truth and missing values

An expression is evaluated as a condition with three values: true, false and unknown. A value is missing when a path leads nowhere or the value is null. A comparison is unknown when a value it needs is missing, when it compares types that do not fit, or when a number is NaN.

- `not` of unknown is unknown.
- `and` and `or` follow Kleene logic: `false and unknown` is false, `true or unknown` is true, and every other mix with unknown is unknown.
- `== null` tests for a missing value and is never unknown: it is true when the value is missing, and `!= null` is false then. Any other operator with `null` is unknown.
- A condition matches only when it is true. So `Power > 5` matches no subject without a `Power`, and neither does `not (Power > 5)`.

`ParsedExpression.EvaluateCondition` gives `true`, `false` or `null` for unknown.

## Comparisons

Two values that are not null compare by these rules, in the class `ExpressionComparer`:

- **Numbers** of any numeric type compare numerically. A `float` compares with any `double`, such as a literal, as a `float`, so a value of `0.1f` equals the literal `0.1`.
- **NaN** never compares: the comparison is unknown.
- **Enums** compare with a text by name without regard to case, for `==` and `!=` only. Two values of the same enum type compare by order with every operator.
- **Texts** compare without regard to case for `==` and `!=`, and by ordinal order for `<`, `<=`, `>` and `>=`.
- **Dates**: a `DateTime` or `DateTimeOffset` compares with a text literal in ISO 8601. A text without an offset is read as UTC, and so is a `DateTime` without a kind.
- **Booleans** compare with booleans for `==` and `!=` only.
- **Every other pair** has no order, so the comparison is unknown.

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
