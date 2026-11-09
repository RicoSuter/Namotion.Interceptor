using System.Collections.Immutable;

namespace HomeBlaze.Storage.Abstractions;

/// <summary>
/// The exception that <see cref="IConfigurationWriter.WriteConfigurationAsync"/> and
/// <see cref="IConfigurationWriter.ThrowIfWriteRefused"/> throw when the configuration that contains a subject is not
/// written because the file it was read from has issues, such as a value that could not be read, which writing the
/// configuration would lose. Nothing is written. The message names the issues and how to correct them.
/// </summary>
public sealed class ConfigurationWriteRefusedException : InvalidOperationException
{
    /// <summary>Creates the exception with its message and the issues of the file.</summary>
    public ConfigurationWriteRefusedException(string message, ImmutableArray<string> issues)
        : base(message)
    {
        Issues = issues;
    }

    /// <summary>The issues of the file, at least one.</summary>
    public ImmutableArray<string> Issues { get; }
}
