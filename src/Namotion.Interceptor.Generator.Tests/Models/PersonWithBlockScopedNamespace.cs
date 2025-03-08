using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Generator.Tests.Models
{
    [InterceptorSubject]
    public partial class PersonWithBlockScopedNamespace
    {
        public partial string? FirstName { get; set; }
        
        internal partial string LastName { get; set; }
    }
}