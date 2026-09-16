namespace Namotion.Interceptor;

/// <summary>
/// Provides outcome-aware replay for supported subject properties.
/// </summary>
public interface ISubjectPropertyReplay
{
    /// <summary>
    /// Returns whether the property supports exact replay outcomes without invoking its getter, setter or hooks.
    /// </summary>
    bool CanReplayProperty(string propertyName);

    /// <summary>
    /// Replays the property setter, including its hooks and interceptors. Resets the outcome before the attempt
    /// and preserves acceptance and mutation information if the write throws. Revalidates support when invoked;
    /// a previous capability check does not guarantee that property metadata is unchanged.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Thrown when the property does not support exact replay outcomes.
    /// </exception>
    void ReplayProperty(string propertyName, object? value, ref PropertyReplayOutcome outcome);
}
