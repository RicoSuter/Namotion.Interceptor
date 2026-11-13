namespace HomeBlaze.Automation.Runtime;

/// <summary>What an argument key of an operation action names, see <see cref="ArgumentBinder.MatchKey"/>.</summary>
internal enum ArgumentKeyMatch
{
    /// <summary>A parameter that requires input.</summary>
    Parameter,

    /// <summary>A member of a record parameter, as <c>parameter.Member</c>.</summary>
    Member,

    /// <summary>No parameter that requires input, or one that is no record.</summary>
    NoParameter,

    /// <summary>No member of the record parameter before the dot.</summary>
    NoMember
}
