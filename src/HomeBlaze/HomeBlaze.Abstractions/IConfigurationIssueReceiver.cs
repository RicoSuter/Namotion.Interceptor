namespace HomeBlaze.Abstractions;

/// <summary>
/// A configurable subject that is told the issues of the configuration file it was last read from, such as a value that
/// could not be read or a key at the top level that names none of its configuration properties, so that it can show
/// them, for example as a configuration error. For any other subject such a key is ignored.
/// </summary>
public interface IConfigurationIssueReceiver
{
    /// <summary>
    /// Takes the issues of the configuration that was read last, none when it was read in full, in an array that is the
    /// receiver's own. Called after the configuration properties were set, and before
    /// <see cref="IConfigurable.ApplyConfigurationAsync"/> when that follows.
    /// </summary>
    void SetConfigurationIssues(string[] issues);
}
