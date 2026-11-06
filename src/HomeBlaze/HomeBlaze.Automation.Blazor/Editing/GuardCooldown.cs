namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// The cooldown control of a rule as a view over its guard: a cooldown of 30 minutes is the guard
/// <c>./LastTriggeredAt == null or now - ./LastTriggeredAt &gt; 30m</c>, after the other condition joined with
/// <c>and</c>. A guard is split into a condition and a cooldown only when it still has exactly that form; any other guard
/// is the condition as it is.
/// </summary>
internal static class GuardCooldown
{
    private const string CooldownPrefix = "./LastTriggeredAt == null or now - ./LastTriggeredAt > ";
    private const string Separator = " and (";

    public static string? Combine(string? condition, TimeSpan? cooldown)
    {
        var hasCondition = !string.IsNullOrWhiteSpace(condition);
        if (cooldown is not { } duration || duration <= TimeSpan.Zero)
        {
            return hasCondition ? condition : null;
        }

        var cooldownGuard = CooldownPrefix + AutomationDuration.Format(duration);
        return hasCondition ? $"{Wrap(condition!)}{Separator}{cooldownGuard})" : cooldownGuard;
    }

    public static (string? Condition, TimeSpan? Cooldown) Split(string? guard)
    {
        if (string.IsNullOrWhiteSpace(guard))
        {
            return (null, null);
        }

        if (TryParseCooldown(guard, out var alone))
        {
            return (null, alone);
        }

        var index = guard.LastIndexOf(Separator, StringComparison.Ordinal);
        if (index > 0 && guard.EndsWith(')') && TryParseCooldown(guard[(index + Separator.Length)..^1], out var cooldown) && IsCombinedCondition(guard[..index]))
        {
            return (Unwrap(guard[..index]), cooldown);
        }

        return (guard, null);
    }

    // Combine joins the cooldown to a condition with "or" only inside parentheses; "a or b and (cooldown)" reads as "a or (b and cooldown)".
    private static bool IsCombinedCondition(string condition) => IsBalanced(condition) && !HasTopLevelOr(condition);

    private static bool TryParseCooldown(string text, out TimeSpan cooldown)
    {
        cooldown = TimeSpan.Zero;
        return text.StartsWith(CooldownPrefix, StringComparison.Ordinal) &&
            AutomationDuration.TryParse(text[CooldownPrefix.Length..], out cooldown) &&
            cooldown > TimeSpan.Zero;
    }

    // Parentheses keep an "or" of the condition from taking the cooldown into one of its branches. A condition that is
    // wrapped already is wrapped again, so that unwrapping gives back exactly what was written.
    private static string Wrap(string condition) => NeedsParentheses(condition) ? $"({condition})" : condition;

    private static string Unwrap(string condition) =>
        IsWrapped(condition) && NeedsParentheses(condition[1..^1]) ? condition[1..^1] : condition;

    private static bool NeedsParentheses(string condition) => IsWrapped(condition) || HasTopLevelOr(condition);

    private static bool IsWrapped(string text) =>
        text.Length >= 2 &&
        text[0] == '(' &&
        Unquoted(text).FirstOrDefault(character => character.Value == ')' && character.Depth == 1).Index == text.Length - 1;

    private static bool HasTopLevelOr(string text) =>
        Unquoted(text).Any(character => character.Depth == 0 && IsOrAt(text, character.Index));

    private static bool IsOrAt(string text, int index) =>
        text.AsSpan(index).StartsWith("||") ||
        (IsWordStart(text, index) && text.AsSpan(index).StartsWith("or", StringComparison.OrdinalIgnoreCase) && IsWordEnd(text, index + 2));

    private static bool IsWordStart(string text, int index) => index == 0 || char.IsWhiteSpace(text[index - 1]) || text[index - 1] == ')';

    private static bool IsWordEnd(string text, int index) => index == text.Length || char.IsWhiteSpace(text[index]) || text[index] == '(';

    private static bool IsBalanced(string text)
    {
        var depth = 0;
        foreach (var character in Unquoted(text))
        {
            depth += character.Value switch
            {
                '(' => 1,
                ')' => -1,
                _ => 0
            };

            if (depth < 0)
            {
                return false;
            }
        }

        return depth == 0;
    }

    /// <summary>The characters outside quotes with the depth of parentheses they stand in; a parenthesis belongs to its inside.</summary>
    private static IEnumerable<(int Index, char Value, int Depth)> Unquoted(string text)
    {
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                quote = character == quote ? '\0' : quote;
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else
            {
                depth += character == '(' ? 1 : 0;
                yield return (index, character, depth);
                depth -= character == ')' ? 1 : 0;
            }
        }
    }
}
