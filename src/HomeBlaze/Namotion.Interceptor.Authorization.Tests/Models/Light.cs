using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Authorization.Tests.Models;

[InterceptorSubject]
public partial class Light : ILight
{
    public partial bool IsOn { get; set; }

    public partial int Brightness { get; set; }
}
