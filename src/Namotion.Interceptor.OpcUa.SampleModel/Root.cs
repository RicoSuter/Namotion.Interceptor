using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.SampleModel;

[InterceptorSubject]
public partial class Root
{
    [SourceName("opc", "Name")]
    public partial string Name { get; set; }
    
    [SourceName("opc", "Number")]
    public partial decimal Number { get; set; }
    
    [SourcePath("opc", "Person")]
    public partial Person? Person { get; set; }

    public Root()
    {
        Name = "My root name";
    }
}