namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>Ordinal wildcard matching where <c>*</c> stands for any text, including none. No other wildcard exists.</summary>
internal static class Glob
{
    public static bool IsMatch(ReadOnlySpan<char> pattern, ReadOnlySpan<char> text)
    {
        // Greedy match with one level of backtracking to the last star, the classic linear-time glob.
        var patternIndex = 0;
        var textIndex = 0;
        var lastStar = -1;
        var textAtLastStar = 0;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                lastStar = patternIndex++;
                textAtLastStar = textIndex;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == text[textIndex])
            {
                patternIndex++;
                textIndex++;
            }
            else if (lastStar >= 0)
            {
                patternIndex = lastStar + 1;
                textIndex = ++textAtLastStar;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
        {
            patternIndex++;
        }

        return patternIndex == pattern.Length;
    }

    public static bool IsMatchAny(string[] patterns, string text)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, text))
            {
                return true;
            }
        }

        return false;
    }
}
