using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Virtual;

/// <summary>
/// An entry of the properties of a virtual subject: an expression property with <see cref="Expression"/>, a binding
/// property with <see cref="Path"/>, or a nested subject with <see cref="Properties"/>, exactly one of them.
/// </summary>
public sealed record VirtualProperty
{
    /// <summary>The expression of a read-only property, recalculated when a value it reads changes.</summary>
    [Configuration]
    public string? Expression { get; init; }

    /// <summary>The path of the property that a binding property reads and writes.</summary>
    [Configuration]
    public string? Path { get; init; }

    /// <summary>The type of the value of an expression or binding property.</summary>
    [Configuration]
    public VirtualDataType? DataType { get; init; }

    /// <summary>Makes the property state: shown, recorded in history and exposed by servers.</summary>
    [Configuration]
    public VirtualState? State { get; init; }

    /// <summary>The registry attributes of an expression or binding property, by name.</summary>
    [Configuration]
    public IReadOnlyDictionary<string, VirtualProperty>? Attributes { get; init; }

    /// <summary>The entries of a nested subject, by name.</summary>
    [Configuration]
    public IReadOnlyDictionary<string, VirtualProperty>? Properties { get; init; }
}
