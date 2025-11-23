using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Sources.Paths.Attributes;

namespace Namotion.Interceptor.OpcUa.SampleModel;

[InterceptorSubject]
public partial class Root
{
    [SourcePath("opc", "Name")]
    public partial string Name { get; set; }
    
    [SourcePath("opc", "Number")]
    public partial decimal Number { get; set; }
    
    [SourcePath("opc", "Persons")]
    public partial Person[] Persons { get; set; }

    public Root()
    {
        Name = "My root name";
        Persons = [];
    }
}