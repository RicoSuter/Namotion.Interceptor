using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models1
{
    [InterceptorSubject]
    public partial class Calculator
    {
        public int InternalResult { get; private set; }
        
        public int Foo { get; private set; }
    }
}

namespace Namotion.Interceptor.Generator.Tests.Models2
{
    [InterceptorSubject]
    public partial class Calculator
    {
        public int InternalResult { get; private set; }
        
        public int Bar { get; private set; }
    }
}