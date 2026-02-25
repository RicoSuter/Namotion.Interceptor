using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models;

public partial class OuterClass
{
    [InterceptorSubject]
    public partial class SingleNestedSubject
    {
        public partial string Name { get; set; }
    }

    public partial class MiddleClass
    {
        [InterceptorSubject]
        public partial class DeepNestedSubject
        {
            public partial int Value { get; set; }
        }
    }
}

public partial class OuterSubject
{
    [InterceptorSubject]
    public partial class NestedInsideSubject
    {
        public partial string Description { get; set; }
    }
}

[InterceptorSubject]
public partial class OuterSubject
{
    public partial string OuterName { get; set; }
}
