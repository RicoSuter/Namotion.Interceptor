using System.Diagnostics.CodeAnalysis;
using HomeBlaze.Abstractions;
using HomeBlaze.Services.Display;
using HomeBlaze.Services.Metadata;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Registry.Paths;

namespace HomeBlaze.AI.Mcp;

/// <summary>
/// Exposes [State] properties via MCP, and metadata where it has a value: that of a subject as its property
/// <c>HomeBlaze:Metadata</c>, and that of a property by the path of its attribute (<c>Temperature@HomeBlaze:Metadata</c>).
/// Uses slash separator for InlinePaths compatibility (file names with dots like "Dashboard.md" are valid single
/// segments with slash notation).
/// </summary>
public class StateAttributePathProvider : PathProviderBase
{
    public override char PathSeparator => ExpressionDisplay.PathSeparator;

    public override bool TryGetPropertySegment(RegisteredSubjectProperty property, [NotNullWhen(true)] out string? segment)
    {
        segment = property.TryGetAttribute(KnownAttributes.State) is not null || property.CanContainSubjects || IsMetadataWithValue(property)
            ? property.Name
            : null;
        return segment is not null;
    }

    // Removed metadata stays as a null value, as the registry cannot remove a property; it is listed as no metadata.
    private static bool IsMetadataWithValue(RegisteredSubjectProperty property) => property.IsMetadata() && property.GetValue() is not null;
}
