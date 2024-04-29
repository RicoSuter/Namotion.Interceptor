namespace Namotion.Proxy.OpcUa.Annotations;

public class OpcUaPropertyReferenceTypeAttribute : Attribute
{
    public OpcUaPropertyReferenceTypeAttribute(string type)
    {
        Type = type;
    }

    public string Type { get; }
}
