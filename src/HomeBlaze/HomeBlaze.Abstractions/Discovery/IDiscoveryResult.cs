namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A device that a <see cref="SubjectDiscovery{TSubject, TResult}"/> found, with what the user is shown for it.
/// </summary>
public interface IDiscoveryResult
{
    /// <summary>
    /// Identifies the device within the subject discovery that found it, the same in every scan. Keys are compared ordinally, so a subject discovery normalizes the case where the identity of the device ignores it.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// The name the user is shown for the device.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// A second line for the user, such as the host address, or null.
    /// </summary>
    string? Detail { get; }
}
