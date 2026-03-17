using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Opc.Ua;

namespace Namotion.Interceptor.OpcUa.Tests.Mapping;

/// <summary>
/// Test model for AttributeOpcUaNodeMapper unit tests.
/// </summary>
[InterceptorSubject]
public partial class AttributeMapperTestModel
{
    /// <summary>Simple property with just OpcUaNode attribute.</summary>
    [OpcUaNode("SimpleProp", "http://test/")]
    public partial string SimpleProp { get; set; }

    /// <summary>Property with explicit NodeIdentifier for Priority 1 matching.</summary>
    [OpcUaNode("NodeIdProp", NodeIdentifier = "ns=2;s=MyExplicitNodeId", NodeNamespaceUri = "http://myserver/")]
    public partial double NodeIdProp { get; set; }

    /// <summary>Property with sampling/queue settings.</summary>
    [OpcUaNode("MonitoredProp", SamplingInterval = 500, QueueSize = 10)]
    public partial double MonitoredProp { get; set; }

    /// <summary>Property with data change filter settings.</summary>
    [OpcUaNode("FilteredProp", DataChangeTrigger = DataChangeTrigger.StatusValueTimestamp, DeadbandType = DeadbandType.Absolute, DeadbandValue = 0.5)]
    public partial double FilteredProp { get; set; }

    /// <summary>Property with discard oldest false.</summary>
    [OpcUaNode("QueueProp", DiscardOldest = DiscardOldestMode.False)]
    public partial int QueueProp { get; set; }

    /// <summary>Property with type definition.</summary>
    [OpcUaNode("TypedProp", TypeDefinition = "AnalogItemType", TypeDefinitionNamespace = "http://opcfoundation.org/UA/")]
    public partial double TypedProp { get; set; }

    /// <summary>Property with OpcUaReference attribute.</summary>
    [OpcUaNode("RefProp")]
    [OpcUaReference("HasComponent")]
    public partial AttributeMapperTestChild RefProp { get; set; }

    /// <summary>Property with modelling rule.</summary>
    [OpcUaNode("MandatoryProp", ModellingRule = ModellingRule.Mandatory)]
    public partial string MandatoryProp { get; set; }

    /// <summary>Property with NodeClass override.</summary>
    [OpcUaNode("VariableClassProp", NodeClass = OpcUaNodeClass.Variable)]
    public partial AttributeMapperVariableChild VariableClassProp { get; set; }

    /// <summary>Property with no OPC UA attributes (for negative testing).</summary>
    public partial string PlainProp { get; set; }

    /// <summary>Property with EventNotifier explicitly set to 0 (no events).</summary>
    [OpcUaNode("EventNotifierZeroProp", EventNotifier = 0)]
    public partial int EventNotifierZeroProp { get; set; }

    /// <summary>Property with DataType and DataTypeNamespace.</summary>
    [OpcUaNode("DataTypeProp", DataType = "CustomDataType", DataTypeNamespace = "http://custom/datatypes/")]
    public partial double DataTypeProp { get; set; }

    /// <summary>Property with ReferenceTypeNamespace via OpcUaReferenceAttribute.</summary>
    [OpcUaNode("RefAttrProp")]
    [OpcUaReference("HasComponent", ReferenceTypeNamespace = "http://custom/reftypes/")]
    public partial AttributeMapperTestChild RefAttrProp { get; set; }

    /// <summary>Property with ItemReferenceType and ItemReferenceTypeNamespace.</summary>
    [OpcUaNode("CollectionProp")]
    [OpcUaReference("HasComponent", ItemReferenceType = "CustomItemRef", ItemReferenceTypeNamespace = "http://custom/itemrefs/")]
    public partial List<AttributeMapperTestChild>? CollectionProp { get; set; }

    public AttributeMapperTestModel()
    {
        SimpleProp = "";
        NodeIdProp = 0;
        MonitoredProp = 0;
        FilteredProp = 0;
        QueueProp = 0;
        TypedProp = 0;
        RefProp = new AttributeMapperTestChild();
        MandatoryProp = "";
        VariableClassProp = new AttributeMapperVariableChild();
        PlainProp = "";
        EventNotifierZeroProp = 0;
        DataTypeProp = 0;
        RefAttrProp = new AttributeMapperTestChild();
        CollectionProp = [];
    }
}

[InterceptorSubject]
public partial class AttributeMapperTestChild
{
    [OpcUaNode("Value")]
    public partial double Value { get; set; }

    public AttributeMapperTestChild()
    {
        Value = 0;
    }
}

[InterceptorSubject]
[OpcUaNode("AttributeMapperVariableChild", NodeClass = OpcUaNodeClass.Variable)]
public partial class AttributeMapperVariableChild
{
    [OpcUaNode("Value")]
    [OpcUaValue]
    public partial double Value { get; set; }

    public AttributeMapperVariableChild()
    {
        Value = 0;
    }
}

/// <summary>Invalid model: has [OpcUaValue] without NodeClass = Variable on containing class.</summary>
[InterceptorSubject]
public partial class AttributeMapperInvalidValueModel
{
    [OpcUaValue]
    public partial double InvalidValue { get; set; }

    public AttributeMapperInvalidValueModel()
    {
        InvalidValue = 0;
    }
}

/// <summary>Child item for collection/dictionary tests with class-level OpcUaNode attribute.</summary>
[InterceptorSubject]
[OpcUaNode("AttributeMapperCollectionChild", TypeDefinition = "CollectionItemType")]
public partial class AttributeMapperCollectionChild
{
    public partial double Value { get; set; }

    public AttributeMapperCollectionChild()
    {
        Value = 0;
    }
}

/// <summary>Parent model with collection and dictionary properties for class-level config tests.</summary>
[InterceptorSubject]
public partial class AttributeMapperCollectionParent
{
    public partial List<AttributeMapperCollectionChild>? Items { get; set; }
    public partial Dictionary<string, AttributeMapperCollectionChild>? ItemsByKey { get; set; }
    public partial AttributeMapperCollectionChild[]? ItemsArray { get; set; }

    public AttributeMapperCollectionParent()
    {
        Items = [];
        ItemsByKey = [];
        ItemsArray = [];
    }
}
