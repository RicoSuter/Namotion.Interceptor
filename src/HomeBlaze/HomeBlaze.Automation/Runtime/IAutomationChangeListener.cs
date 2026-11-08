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
    /// A property on the graph route of the listener, or for a listener with an unbounded graph route any property, that
    /// holds subjects changed or was added to a subject at run time, caused at <paramref name="chainDepth"/>. The listener tells <see cref="AutomationChangeDispatcher.Registration.OnGraphChangeReleased"/>
    /// once it no longer has the graph change queued; until then a later graph change may be left out.
    /// </summary>
    void OnGraphChanged(int chainDepth);
}
