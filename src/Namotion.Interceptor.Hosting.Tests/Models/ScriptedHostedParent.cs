using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Hosting.Tests.Models;

[InterceptorSubject]
public partial class ScriptedHostedParent
{
    public partial ScriptedHostedSubject? Child { get; set; }
}
