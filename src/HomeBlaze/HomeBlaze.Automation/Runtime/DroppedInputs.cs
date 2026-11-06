using Namotion.Interceptor;

namespace HomeBlaze.Automation.Runtime;

/// <summary>
/// The dropped inputs that the worker can make up for: the newest change of each property, the activations and whether
/// a graph change was dropped. Filled under the lock of <see cref="AutomationInputQueue"/> until the worker takes it.
/// </summary>
internal sealed class DroppedInputs
{
    private readonly Dictionary<PropertyReference, PropertyChangedInput> _changes = new();
    private readonly List<ActivationInput> _activations = [];

    public IReadOnlyCollection<PropertyChangedInput> Changes => _changes.Values;

    public IReadOnlyList<ActivationInput> Activations => _activations;

    public bool HasGraphChange { get; private set; }

    /// <summary>The drops counted when the worker took these inputs, of every kind of input.</summary>
    public QueueFaults Faults { get; set; }

    /// <summary>Whether an input was dropped because the queue was full.</summary>
    public bool HasOverload => Faults.Overloaded > 0 || HasOverloadedInputs;

    /// <summary>Whether a change or an activation was dropped because the queue was full.</summary>
    public bool HasOverloadedInputs => _changes.Values.Any(change => !IsChainDrop(change)) || _activations.Exists(activation => !IsChainDrop(activation));

    /// <summary>The deepest chain of the changes and activations dropped because the queue was full, 0 without one.</summary>
    public int OverloadDepth => _changes.Values.Where(change => !IsChainDrop(change)).Select(change => change.ChainDepth)
        .Concat(_activations.Where(activation => !IsChainDrop(activation)).Select(activation => activation.ChainDepth))
        .DefaultIfEmpty(0)
        .Max();

    /// <summary>Whether the input was dropped because it continued a feedback chain beyond the limit.</summary>
    public static bool IsChainDrop(AutomationInput input) => input.ChainDepth > AutomationInputQueue.MaxChainDepth;

    public void Add(AutomationInput input)
    {
        switch (input)
        {
            case PropertyChangedInput change:
                if (change.Revision == 0 || !_changes.TryGetValue(change.Property, out var kept) || kept.Revision < change.Revision)
                {
                    _changes[change.Property] = change;
                }

                break;
            case ActivationInput activation:
                _activations.Add(activation);
                break;
            case GraphChangedInput:
                HasGraphChange = true;
                break;
        }
    }
}
