using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tests;

[InterceptorSubject]
public partial class Car
{
    public partial int Speed { get; set; }
    
    protected partial int ProtectedInternalProperty { get; set; }

    protected int ProtectedProperty { get; set; }

    private partial int PrivateInternalProperty { get; set; }

    private int PrivateProperty { get; set; }
}