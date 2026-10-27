using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Validation.Tests.Models;

[InterceptorSubject]
public partial class Motor
{
    public Motor()
    {
        MaxAllowedSpeed = 100;
    }

    public partial int MaxAllowedSpeed { get; set; }

    public partial int MotorSpeed { get; set; }

    /// <summary>Adds one to every MotorSpeed write, so a commit applies a different value than it captured.</summary>
    public bool IncrementSpeedOnWrite { get; set; }

    /// <summary>MotorSpeed written from the MaxAllowedSpeed change hook, like a change handler would.</summary>
    public int? SpeedToWriteOnLimitChange { get; set; }

    partial void OnMotorSpeedChanging(ref int newValue, ref bool cancel)
    {
        if (IncrementSpeedOnWrite)
        {
            newValue++;
        }
    }

    partial void OnMaxAllowedSpeedChanged(int newValue)
    {
        if (SpeedToWriteOnLimitChange is { } speed)
        {
            MotorSpeed = speed;
        }
    }
}
