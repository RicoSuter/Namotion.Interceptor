namespace Namotion.Proxy.OpcUa.Annotations;

public class OpcUaPropertyItemReferenceTypeAttribute : Attribute
{
    public OpcUaPropertyItemReferenceTypeAttribute(string type)
    {
        Type = type;
    }

    public string Type { get; }
}
