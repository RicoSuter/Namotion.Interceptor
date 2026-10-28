using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class TextField
{
    public partial string? Text { get; set; }

    public partial int MaxLength { get; set; }
}
