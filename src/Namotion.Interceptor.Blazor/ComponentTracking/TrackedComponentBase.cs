using Microsoft.AspNetCore.Components;

namespace Namotion.Interceptor.Blazor.ComponentTracking;

/// <summary>
/// A component that, while component tracking records its renders, ignores a render of its parent that passes
/// unchanged parameters: its parameters are not set again and it does not render.
/// </summary>
/// <remarks>
/// A parameter is unchanged when its value is equal by <see cref="object.Equals(object?)"/> to the value passed
/// before; a delegate, such as a <see cref="RenderFragment"/>, always counts as changed. The component still renders
/// when a property that its render read changes, when a parameter changes and when it calls <c>StateHasChanged</c>.
/// Derive from it only if its render shows nothing else that can change, for example an object passed as a parameter
/// and changed in place without being a subject.
/// </remarks>
public abstract class TrackedComponentBase : ComponentBase
{
    private string[] _names = [];
    private object?[] _values = [];
    private int _count = -1;

    /// <inheritdoc />
    public override Task SetParametersAsync(ParameterView parameters)
    {
        if (!UpdateParameters(parameters) && IsTracked())
        {
            return Task.CompletedTask;
        }

        return base.SetParametersAsync(parameters);
    }

    // Reads of a render that is not recorded, while prerendering or without component tracking, render nothing again.
    private bool IsTracked() =>
        ComponentBaseAccessors.IsSupported &&
        ComponentBaseAccessors.RenderFragmentOf(this).Target is ComponentReadTracker { IsRecording: true };

    // Returns whether a parameter changed, and keeps the parameters if one did.
    private bool UpdateParameters(ParameterView parameters)
    {
        if (AreUnchanged(parameters))
        {
            return false;
        }

        var index = 0;
        foreach (var parameter in parameters)
        {
            if (index == _names.Length)
            {
                var length = Math.Max(4, index * 2);
                Array.Resize(ref _names, length);
                Array.Resize(ref _values, length);
            }

            _names[index] = parameter.Name;
            _values[index] = parameter.Value;
            index++;
        }

        Array.Clear(_values, index, Math.Max(0, _count - index));
        _count = index;
        return true;
    }

    private bool AreUnchanged(ParameterView parameters)
    {
        var index = 0;
        foreach (var parameter in parameters)
        {
            if (index >= _count ||
                !string.Equals(_names[index], parameter.Name, StringComparison.Ordinal) ||
                !AreEqual(_values[index], parameter.Value))
            {
                return false;
            }

            index++;
        }

        return index == _count;
    }

    private static bool AreEqual(object? oldValue, object? newValue)
    {
        if (oldValue is null || newValue is null)
        {
            return oldValue is null && newValue is null;
        }

        // Content can read state of its creator that changed without a parameter or a tracked property.
        if (oldValue is Delegate)
        {
            return false;
        }

        return ReferenceEquals(oldValue, newValue) ||
            (oldValue.GetType() == newValue.GetType() && oldValue.Equals(newValue));
    }
}
