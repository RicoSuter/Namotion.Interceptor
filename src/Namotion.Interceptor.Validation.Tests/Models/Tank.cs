using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

/// <summary>A tank with derived properties, kept apart from the other models so their validators do not see its recalculations.</summary>
[InterceptorSubject]
public partial class Tank
{
    public Tank()
    {
        Capacity = 100;
    }

    public partial int Capacity { get; set; }

    public partial int Level { get; set; }

    [Derived]
    public int Free => Capacity - Level;

    /// <summary>Derived from <see cref="Free"/>; the getter throws while <see cref="Capacity"/> is 0.</summary>
    [Derived]
    public int FreePercent => Free * 100 / Capacity;
}
