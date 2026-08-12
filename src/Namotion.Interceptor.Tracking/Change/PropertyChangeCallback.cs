namespace Namotion.Interceptor.Tracking.Change;

/// <summary>
/// Delegate form of <see cref="IPropertyChangeObserver"/>, and the contract splits the same way: subscribed
/// without a scheduler it runs on the writing thread and must not throw, subscribed with one it runs on the
/// scheduler, may throw, and is serialized within a single subscription but not across several that share it.
/// See <see cref="IPropertyChangeObserver"/> for the detail.
/// </summary>
public delegate void PropertyChangeCallback(in SubjectPropertyChange change);
