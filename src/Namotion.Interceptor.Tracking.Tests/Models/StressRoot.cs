using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// A parent with single-reference slots and an array of <see cref="StressNode"/> children, whose
/// derived properties read the properties of those children.
/// </summary>
[InterceptorSubject]
public partial class StressRoot
{
    public StressRoot()
    {
        Items = [];
    }

    public partial StressNode? First { get; set; }

    public partial StressNode? Second { get; set; }

    public partial StressNode[] Items { get; set; }

    [Derived]
    public int FirstValue => First?.Value ?? -1;

    [Derived]
    public int SecondOffset => Second?.Offset ?? -1;

    [Derived]
    public int ItemsShifted
    {
        get
        {
            var total = 0;
            foreach (var item in Items)
            {
                total += item.Shifted;
            }

            return total;
        }
    }
}
