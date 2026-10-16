using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Attributes;

namespace Coffee;

#region Recipe
[InterceptorSubject]
public partial class Recipe
{
    public partial string Name { get; set; }

    [Range(10, 300)]
    public partial int WaterAmount { get; set; }

    [Range(85.0, 96.0)]
    public partial double Temperature { get; set; }

    public Recipe()
    {
        Name = string.Empty;
        WaterAmount = 40;
        Temperature = 93;
    }
}
#endregion
