namespace Namotion.Interceptor.Tracking.Change;

/// <summary>Delegate form of <see cref="IPropertyChangeObserver"/>. Same contract applies.</summary>
public delegate void PropertyChangeCallback(in SubjectPropertyChange change);
