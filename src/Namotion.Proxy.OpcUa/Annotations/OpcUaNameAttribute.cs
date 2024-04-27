namespace Namotion.Proxy.OpcUa.Annotations;

public class OpcUaNameAttribute : Attribute
{
    public OpcUaNameAttribute(string name, string ns)
    {
        Name = name;

        Namespace = ns;
    }

    public string Name { get; }

    public string Namespace { get; }
}
