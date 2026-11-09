namespace HomeBlaze.Automation.Blazor.Editing;

/// <summary>The rules that change to a state, are active in it, and leave it, some of them from any state.</summary>
internal sealed record StateUsage(IReadOnlyList<RuleDraft> EnteredBy, IReadOnlyList<RuleDraft> ActiveIn, IReadOnlyList<RuleDraft> LeftBy)
{
    /// <summary>The number of rules that would refer to a state that no longer exists if it were deleted.</summary>
    public int UseCount { get; } = EnteredBy.Count + ActiveIn.Count(rule => !EnteredBy.Contains(rule));

    public bool IsUsed => UseCount > 0;
}
