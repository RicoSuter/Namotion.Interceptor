using HomeBlaze.Abstractions.Attributes;

namespace HomeBlaze.Abstractions.Metadata;

/// <summary>
/// Describes a parameter of a subject method.
/// </summary>
public class MethodParameter
{
    /// <summary>
    /// The parameter name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The CLR type of the parameter.
    /// </summary>
    public required Type Type { get; init; }

    /// <summary>
    /// The physical unit of the parameter value, if applicable.
    /// </summary>
    public StateUnit? Unit { get; init; }

    /// <summary>
    /// Whether this parameter is resolved from the DI container via [FromServices].
    /// </summary>
    public bool IsFromServices { get; init; }

    /// <summary>
    /// Whether this parameter accepts null (nullable reference type or <see cref="Nullable{T}"/>).
    /// </summary>
    public bool IsNullable { get; init; }

    /// <summary>
    /// Whether this parameter is provided by the runtime (e.g., <see cref="CancellationToken"/>).
    /// Runtime-provided parameters require neither user input nor DI registration.
    /// </summary>
    public bool IsRuntimeProvided { get; init; }

    /// <summary>
    /// Whether this parameter requires user input
    /// (i.e., not runtime-provided and not from DI).
    /// </summary>
    public bool RequiresInput => !IsRuntimeProvided && !IsFromServices;
}
