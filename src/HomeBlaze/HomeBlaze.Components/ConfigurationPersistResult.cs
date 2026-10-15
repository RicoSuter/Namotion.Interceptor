namespace HomeBlaze.Components;

/// <summary>
/// The result of <see cref="ConfigurationPersistence.PersistOwnerAsync"/>.
/// </summary>
public enum ConfigurationPersistResult
{
    /// <summary>The configuration owner was written and applied.</summary>
    Written,

    /// <summary>The configuration writer wrote nothing; the owner was applied, so the change is applied but not saved.</summary>
    NotWritten,

    /// <summary>The subject is part of no configuration; nothing was written or applied.</summary>
    NoOwner,
}
