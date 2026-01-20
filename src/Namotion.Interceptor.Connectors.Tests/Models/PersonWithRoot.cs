using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Connectors.Tests.Models;

/// <summary>
/// Root container for PersonWithRoot hierarchy.
/// </summary>
[InterceptorSubject]
public partial class PersonRoot
{
    public partial string? Name { get; set; }

    public partial PersonWithRoot? Person { get; set; }
}

/// <summary>
/// Person model with a Root property that references back up the tree.
/// This is used to test partial update behavior when new subjects have
/// circular references back to ancestors.
/// </summary>
[InterceptorSubject]
public partial class PersonWithRoot
{
    public PersonWithRoot()
    {
        Children = [];
    }

    /// <summary>
    /// Explicit reference back to root - simulates TryGetFirstParent behavior
    /// but as a partial property so it's tracked and serialized.
    /// This creates a circular reference when serializing new subjects.
    /// </summary>
    public partial PersonRoot? Root { get; set; }

    public partial string? FirstName { get; set; }

    public partial string? LastName { get; set; }

    public partial PersonWithRoot? Father { get; set; }

    public partial PersonWithRoot? Mother { get; set; }

    public partial List<PersonWithRoot> Children { get; set; }
}
