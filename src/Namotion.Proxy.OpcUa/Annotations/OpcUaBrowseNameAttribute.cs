namespace Namotion.Proxy.OpcUa.Annotations;

public class OpcUaBrowseNameAttribute : Attribute
{
    public OpcUaBrowseNameAttribute(string name, string ns)
    {
        Name = name;

        Namespace = ns;
    }

    public string Name { get; }

    public string Namespace { get; }
}
