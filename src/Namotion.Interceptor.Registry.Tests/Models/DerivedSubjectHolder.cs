using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Registry.Tests.Models;

/// <summary>
/// Holds a person in an intercepted property and returns the same person from a non-partial
/// [Derived] getter, so a test can assert which properties the registry records as parents.
/// </summary>
[InterceptorSubject]
public partial class DerivedSubjectHolder
{
    public partial DerivedSubjectHolder? Child { get; set; }

    public partial Person? Stored { get; set; }

    public partial bool SelectStored { get; set; }

    [Derived]
    public Person? Selected => SelectStored ? Stored : null;
}
