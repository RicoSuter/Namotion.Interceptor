using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Holds a person in an intercepted property and returns the same person from a non-partial
/// [Derived] getter, so a test can tell a stored reference apart from a computed one.
/// </summary>
[InterceptorSubject]
public partial class DerivedSubjectHolder
{
    public partial DerivedSubjectHolder? Child { get; set; }

    public partial Person? Stored { get; set; }

    public partial bool SelectStored { get; set; }

    [Derived]
    public Person? Selected => SelectStored ? Stored : null;

    [Derived]
    public partial Person? StoredDerived { get; set; }
}
