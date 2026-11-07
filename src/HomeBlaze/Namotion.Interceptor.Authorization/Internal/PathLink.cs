namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// One link of a path, from the subject that holds a property to the child at it, in the spellings the path reader
/// resolves and, for deny rules, in the structural spellings whether or not they resolve: the property's segment with
/// the index text, and for an [InlinePaths] key the bare key. The member that ends a member path is a link without an
/// index.
/// </summary>
/// <param name="Segment">The property's segment as the provider spells it; null for an [InlinePaths] property whose own segment does not resolve to it because another property's segment shadows it.</param>
/// <param name="Index">The index text as the provider spells it; null for a property without an index.</param>
/// <param name="BareKey">The [InlinePaths] key the reader resolves without the property's segment; null when there is none.</param>
/// <param name="StructuralSegment">The property's segment as the provider spells it, also while another property's segment shadows it.</param>
/// <param name="StructuralBareKey">The [InlinePaths] key when it is one non-empty name without separator or index-open character that differs from the property's segment, also while another property's segment shadows it; null otherwise.</param>
internal readonly record struct PathLink(string? Segment, string? Index, string? BareKey, string StructuralSegment, string? StructuralBareKey)
{
    /// <summary>A link through a property that is not an [InlinePaths] property, which has one spelling.</summary>
    public PathLink(string segment, string? index)
        : this(segment, index, null, segment, null)
    {
    }
}
