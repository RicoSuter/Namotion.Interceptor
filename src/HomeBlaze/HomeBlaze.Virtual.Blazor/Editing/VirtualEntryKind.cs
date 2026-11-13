namespace HomeBlaze.Virtual.Blazor.Editing;

/// <summary>What an entry of the draft is.</summary>
public enum VirtualEntryKind
{
    /// <summary>A nested subject with entries of its own.</summary>
    Subject,

    /// <summary>A read-only property computed by an expression.</summary>
    Expression,

    /// <summary>A property that reads and writes another property.</summary>
    Binding
}
