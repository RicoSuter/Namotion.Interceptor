using Namotion.Interceptor;

namespace HomeBlaze.Abstractions.Discovery;

/// <summary>
/// A device that a <see cref="SubjectDiscovery{TSubject, TResult}"/> found, with what the user is shown for it.
/// Implement <see cref="IDiscoveryResult{TSubject}"/> instead of this interface.
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

    /// <summary>
    /// The items that the subject created for this device includes, such as the speakers of a household, shown under
    /// its row and not selectable on their own; empty by default.
    /// </summary>
    IReadOnlyList<DiscoveredItem> IncludedItems => [];

    /// <summary>Whether <paramref name="subject"/> already represents this device; false for a subject of another type.</summary>
    bool Matches(IInterceptorSubject subject);
}

/// <summary>
/// A device that a <see cref="SubjectDiscovery{TSubject, TResult}"/> found, which a <typeparamref name="TSubject"/>
/// represents.
/// </summary>
/// <typeparam name="TSubject">The type of subject that represents the device.</typeparam>
public interface IDiscoveryResult<in TSubject> : IDiscoveryResult
    where TSubject : IInterceptorSubject
{
    /// <summary>Whether <paramref name="subject"/> already represents this device.</summary>
    bool Matches(TSubject subject);

    bool IDiscoveryResult.Matches(IInterceptorSubject subject) => subject is TSubject typed && Matches(typed);
}
