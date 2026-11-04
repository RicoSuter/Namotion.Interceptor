using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

/// <summary>A motor with a derived property, for the tests of a transaction commit that recalculates it.</summary>
[InterceptorSubject]
public partial class PoweredMotor
{
    public partial int MotorSpeed { get; set; }

    [Derived]
    public int Power => MotorSpeed * 10;
}
