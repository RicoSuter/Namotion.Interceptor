namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>
/// Builds the ids of expression fields for <see cref="PendingRows"/> from keys that survive reordering, adding and
/// removing: the key of the rule, the section of the rule, the <see cref="DraftItem{T}.Id"/> of the entry and the field.
/// </summary>
public static class PendingRowIds
{
    public static string For(string ruleKey, string section, string itemId, string field) =>
        $"{ForRule(ruleKey)}{Escape(section)}/{Escape(itemId)}/{Escape(field)}";

    /// <summary>The prefix of every field of a rule, to clear them when the rule is removed.</summary>
    public static string ForRule(string ruleKey) => $"{Escape(ruleKey)}/";

    /// <summary>The prefix of every field of an entry in a section of a rule, to clear them when the entry is removed.</summary>
    public static string ForItem(string ruleKey, string section, string itemId) => $"{ForRule(ruleKey)}{Escape(section)}/{Escape(itemId)}/";

    private static string Escape(string part) => Uri.EscapeDataString(part);
}
