namespace Namotion.Interceptor.OpcUa.Attributes;

public class OpcUaNodeReferenceTypeAttribute : Attribute
{
    public OpcUaNodeReferenceTypeAttribute(string type)
    {
        Type = type;
    }

    public string Type { get; }
}
