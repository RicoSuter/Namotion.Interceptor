using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

[InterceptorSubject]
public partial class FieldReadingSubject
{
    public Person? Source;

    [Derived]
    public string? SourceName => Source?.FirstName;
}

[InterceptorSubject]
public partial class ReaderRoot
{
    public partial FieldReadingSubject? Reader { get; set; }
}
