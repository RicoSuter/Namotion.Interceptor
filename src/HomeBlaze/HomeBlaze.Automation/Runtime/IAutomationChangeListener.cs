namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Receives the changes that <see cref="AutomationChangeDispatcher"/> routes to it. Called synchronously on the writing
/// thread, inside the property setter, so an implementation only enqueues and never throws.
/// </summary>
internal interface IAutomationChangeListener
{
    /// <summary>A property in the watched set of the listener changed.</summary>
    void OnWatchedPropertyChanged(PropertyChangedInput input);

    /// <summary>A property that holds subjects changed, caused at <paramref name="chainDepth"/>.</summary>
    void OnGraphChanged(int chainDepth);
}
