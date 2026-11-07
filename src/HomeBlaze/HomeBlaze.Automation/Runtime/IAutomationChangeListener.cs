namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// Receives the changes that <see cref="AutomationChangeDispatcher"/> routes to it. Called synchronously on the writing
/// thread, inside the property setter, so an implementation only enqueues and never throws.
/// </summary>
internal interface IAutomationChangeListener
{
    /// <summary>A property in the watched set of the listener changed.</summary>
    void OnWatchedPropertyChanged(PropertyChangedInput input);

    /// <summary>
    /// A property on the graph route of the listener changed, or for a listener without a graph route a property that
    /// holds subjects, caused at <paramref name="chainDepth"/>.
    /// </summary>
    void OnGraphChanged(int chainDepth);
}
