using System.Diagnostics.CodeAnalysis;

namespace HomeBlaze.Components.Operations;

/// <summary>
/// A <see cref="MethodParameterEditor"/> that the parameter dialog renders for parameters whose
/// <see cref="HomeBlaze.Abstractions.Metadata.MethodParameter.Editor"/> is <paramref name="Name"/>, compared ordinally.
/// </summary>
/// <param name="Name">The name of the editor.</param>
/// <param name="EditorType">The type of the component, derived from <see cref="MethodParameterEditor"/>.</param>
public sealed record MethodParameterEditorRegistration(
    string Name,
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    Type EditorType);
