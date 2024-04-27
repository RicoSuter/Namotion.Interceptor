namespace Namotion.Proxy.OpcUa.Annotations;

public class OpcUaReferenceTypeAttribute : Attribute
{
    public OpcUaReferenceTypeAttribute(string type)
    {
        Type = type;
    }

    public string Type { get; }
}
