using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Coffee;

#region BeanHopper
[InterceptorSubject]
public partial class BeanHopper
{
    public partial double Level { get; set; }

    [Range(1, 10)]
    public partial int GrindSize { get; set; }

    public BeanHopper()
    {
        Level = 100;
        GrindSize = 5;
    }
}
#endregion
