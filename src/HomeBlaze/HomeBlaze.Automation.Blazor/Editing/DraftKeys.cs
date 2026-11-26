namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>Creates the keys of new rules and the identifiers of new actions.</summary>
internal static class DraftKeys
{
    private const string Letters = "abcdefghijklmnopqrstuvwxyz";
    private const string LettersAndDigits = Letters + "0123456789";

    /// <summary>A key of four characters that starts with a letter, such as <c>k3f9</c>, that no existing rule has.</summary>
    public static string NewRuleKey(IEnumerable<string> existingKeys, Random? random = null)
    {
        var generator = random ?? Random.Shared;
        var existing = existingKeys.ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            var key = string.Create(4, generator, static (characters, source) =>
            {
                characters[0] = Letters[source.Next(Letters.Length)];
                for (var index = 1; index < characters.Length; index++)
                {
                    characters[index] = LettersAndDigits[source.Next(LettersAndDigits.Length)];
                }
            });

            if (!existing.Contains(key))
            {
                return key;
            }
        }
    }

    /// <summary>The identifier <c>prefix-n</c> with the lowest <c>n</c> from 1 that no existing action has.</summary>
    public static string NewActionId(string prefix, IEnumerable<string> existingIds)
    {
        var existing = existingIds.ToHashSet(StringComparer.Ordinal);
        var number = 1;
        while (existing.Contains($"{prefix}-{number}"))
        {
            number++;
        }

        return $"{prefix}-{number}";
    }
}
