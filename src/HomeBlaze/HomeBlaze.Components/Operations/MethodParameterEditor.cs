using HomeBlaze.Abstractions.Metadata;
using Microsoft.AspNetCore.Components;

namespace HomeBlaze.Components.Operations;

/// <summary>
/// The base of a component that enters the value of an operation parameter whose <see cref="MethodParameter.Editor"/>
/// names it, registered with
/// <see cref="MethodParameterEditorServiceCollectionExtensions.AddMethodParameterEditor{TEditor}"/>. The parameter dialog
/// renders it in place of the field of the parameter type and passes on the value it reports through
/// <see cref="ValueChanged"/>; a value that is null for a parameter that is not nullable keeps the dialog from executing.
/// </summary>
public abstract class MethodParameterEditor : ComponentBase
{
    /// <summary>The parameter whose value is entered.</summary>
    [Parameter, EditorRequired]
    public MethodParameter Parameter { get; set; } = null!;

    /// <summary>The label of the field, with an asterisk when the parameter is required.</summary>
    [Parameter]
    public string Label { get; set; } = string.Empty;

    /// <summary>The value, of the parameter type or null.</summary>
    [Parameter]
    public object? Value { get; set; }

    /// <summary>Raised with the new value, of the parameter type or null.</summary>
    [Parameter]
    public EventCallback<object?> ValueChanged { get; set; }

    /// <summary>The error the dialog shows for the value, or null.</summary>
    [Parameter]
    public string? ErrorText { get; set; }
}
