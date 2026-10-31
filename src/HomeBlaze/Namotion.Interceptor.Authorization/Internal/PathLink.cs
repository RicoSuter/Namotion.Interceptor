namespace Namotion.Interceptor.Authorization.Internal;

/// <summary>
/// One link of a path, from the subject that holds a property to the child at it, in the spellings the path reader
/// resolves: the property's segment with the index text, and for an [InlinePaths] key the bare key when the reader
/// takes it without the property. The member that ends a member path is a link without an index.
/// </summary>
/// <param name="Segment">The property's segment as the provider writes it; null for an [InlinePaths] key whose explicit form does not resolve because another property's segment shadows it.</param>
/// <param name="Index">The index text as the provider writes it; null for a property without an index.</param>
/// <param name="BareKey">The [InlinePaths] key the reader resolves without the property's segment; null when there is none.</param>
internal readonly record struct PathLink(string? Segment, string? Index, string? BareKey);
