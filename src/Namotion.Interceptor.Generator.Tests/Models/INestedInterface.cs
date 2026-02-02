namespace Namotion.Interceptor.Generator.Tests.Models;

public partial class OuterClass
{
    public interface INestedSensor
    {
        double Value { get; set; }

        string Status => Value > 0 ? "Active" : "Inactive";
    }
}
