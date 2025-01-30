namespace Namotion.Interceptor;

public readonly struct ReadPropertyInterception
{
    public PropertyReference Property { get; }

    public ReadPropertyInterception(PropertyReference property)
    {
        Property = property;
    }
}