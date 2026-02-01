using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.OpcUa.Attributes;
using Namotion.Interceptor.OpcUa.Mapping;
using Namotion.Interceptor.Registry.Attributes;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Testing;

/// <summary>
/// Root model for shared server tests. Each test class operates on its own isolated area.
/// </summary>
[InterceptorSubject]
public partial class SharedTestModel
{
    public SharedTestModel()
    {
        ReadWrite = new ReadWriteTestArea();
        Transactions = new TransactionTestArea();
        Nested = new NestedTestArea();
        DataTypes = new DataTypesTestArea();
        Collections = new CollectionsTestArea();
        MultiClient = new MultiClientTestArea();
        ClientToServerSync = new ClientToServerSyncTestArea();
        ServerToClientReference = new ServerToClientReferenceTestArea();
        ServerToClientCollection = new ServerToClientCollectionTestArea();
        ServerToClientDictionary = new ServerToClientDictionaryTestArea();
        ServerToClientSharedSubject = new ServerToClientSharedSubjectTestArea();
        ClientToServerReference = new ClientToServerReferenceTestArea();
        ClientToServerCollection = new ClientToServerCollectionTestArea();
        ClientToServerDictionary = new ClientToServerDictionaryTestArea();
        ClientToServerSharedSubject = new ClientToServerSharedSubjectTestArea();
    }

    [Path("opc", "Connected")]
    public partial bool Connected { get; set; }

    [Path("opc", "ReadWrite")]
    public partial ReadWriteTestArea ReadWrite { get; set; }

    [Path("opc", "Transactions")]
    public partial TransactionTestArea Transactions { get; set; }

    [Path("opc", "Nested")]
    public partial NestedTestArea Nested { get; set; }

    [Path("opc", "DataTypes")]
    public partial DataTypesTestArea DataTypes { get; set; }

    [Path("opc", "Collections")]
    public partial CollectionsTestArea Collections { get; set; }

    [Path("opc", "MultiClient")]
    public partial MultiClientTestArea MultiClient { get; set; }

    [Path("opc", "ClientToServerSync")]
    public partial ClientToServerSyncTestArea ClientToServerSync { get; set; }

    [Path("opc", "ServerToClientReference")]
    public partial ServerToClientReferenceTestArea ServerToClientReference { get; set; }

    [Path("opc", "ServerToClientCollection")]
    public partial ServerToClientCollectionTestArea ServerToClientCollection { get; set; }

    [Path("opc", "ServerToClientDictionary")]
    public partial ServerToClientDictionaryTestArea ServerToClientDictionary { get; set; }

    [Path("opc", "ServerToClientSharedSubject")]
    public partial ServerToClientSharedSubjectTestArea ServerToClientSharedSubject { get; set; }

    [Path("opc", "ClientToServerReference")]
    public partial ClientToServerReferenceTestArea ClientToServerReference { get; set; }

    [Path("opc", "ClientToServerCollection")]
    public partial ClientToServerCollectionTestArea ClientToServerCollection { get; set; }

    [Path("opc", "ClientToServerDictionary")]
    public partial ClientToServerDictionaryTestArea ClientToServerDictionary { get; set; }

    [Path("opc", "ClientToServerSharedSubject")]
    public partial ClientToServerSharedSubjectTestArea ClientToServerSharedSubject { get; set; }
}

/// <summary>
/// Isolated data area for OpcUaReadWriteTests.
/// </summary>
[InterceptorSubject]
public partial class ReadWriteTestArea
{
    public ReadWriteTestArea()
    {
        BasicSync = new BasicSyncArea();
        ArraySync = new ArraySyncArea();
    }

    [Path("opc", "BasicSync")]
    public partial BasicSyncArea BasicSync { get; set; }

    [Path("opc", "ArraySync")]
    public partial ArraySyncArea ArraySync { get; set; }
}

/// <summary>
/// Data for basic property synchronization test.
/// </summary>
[InterceptorSubject]
public partial class BasicSyncArea
{
    public BasicSyncArea()
    {
        Name = "";
    }

    [Path("opc", "Name")]
    public partial string Name { get; set; }

    [Path("opc", "Number")]
    public partial decimal Number { get; set; }
}

/// <summary>
/// Data for array synchronization test.
/// </summary>
[InterceptorSubject]
public partial class ArraySyncArea
{
    public ArraySyncArea()
    {
        ScalarNumbers = [];
        ScalarStrings = [];
    }

    [Path("opc", "ScalarNumbers")]
    public partial int[] ScalarNumbers { get; set; }

    [Path("opc", "ScalarStrings")]
    public partial string[] ScalarStrings { get; set; }
}

/// <summary>
/// Isolated data area for OpcUaTransactionTests.
/// </summary>
[InterceptorSubject]
public partial class TransactionTestArea
{
    public TransactionTestArea()
    {
        SingleProperty = new TransactionSinglePropertyArea();
        MultiProperty = new TransactionMultiPropertyArea();
    }

    [Path("opc", "SingleProperty")]
    public partial TransactionSinglePropertyArea SingleProperty { get; set; }

    [Path("opc", "MultiProperty")]
    public partial TransactionMultiPropertyArea MultiProperty { get; set; }
}

/// <summary>
/// Data for single property transaction test.
/// </summary>
[InterceptorSubject]
public partial class TransactionSinglePropertyArea
{
    public TransactionSinglePropertyArea()
    {
        Name = "";
    }

    [Path("opc", "Name")]
    public partial string Name { get; set; }
}

/// <summary>
/// Data for multi-property transaction test.
/// </summary>
[InterceptorSubject]
public partial class TransactionMultiPropertyArea
{
    public TransactionMultiPropertyArea()
    {
        Name = "";
    }

    [Path("opc", "Name")]
    public partial string Name { get; set; }

    [Path("opc", "Number")]
    public partial decimal Number { get; set; }
}

/// <summary>
/// Isolated data area for nested structure tests.
/// Tests object references, arrays, dictionaries, deep nesting, OpcUaValue pattern, and PropertyAttribute.
/// </summary>
[InterceptorSubject]
public partial class NestedTestArea
{
    public NestedTestArea()
    {
        Person = new NestedPerson();
        People = [];
        PeopleByName = new Dictionary<string, NestedPerson>();
        Sensor = new NestedSensor();
        Number_Unit = "count";
    }

    /// <summary>Object reference test.</summary>
    [Path("opc", "Person")]
    public partial NestedPerson Person { get; set; }

    /// <summary>Array of objects test.</summary>
    [Path("opc", "People")]
    public partial NestedPerson[] People { get; set; }

    /// <summary>Dictionary of objects test.</summary>
    [Path("opc", "PeopleByName")]
    public partial Dictionary<string, NestedPerson>? PeopleByName { get; set; }

    /// <summary>OpcUaValue pattern test - sensor as VariableNode with child properties.</summary>
    [OpcUaNode("Sensor")]
    [OpcUaReference("HasComponent")]
    public partial NestedSensor? Sensor { get; set; }

    /// <summary>PropertyAttribute test - Number variable with Unit subnode.</summary>
    [Path("opc", "Number")]
    public partial decimal Number { get; set; }

    /// <summary>PropertyAttribute - becomes HasProperty subnode of Number.</summary>
    [PropertyAttribute(nameof(Number), "Unit")]
    [Path("opc", "Unit")]
    public partial string Number_Unit { get; set; }
}

/// <summary>
/// Person model for nested structure tests.
/// </summary>
[InterceptorSubject]
public partial class NestedPerson
{
    public NestedPerson()
    {
        FirstName = "";
        LastName = "";
        Scores = [];
        Address = new NestedAddress();
    }

    [Path("opc", "FirstName")]
    public partial string FirstName { get; set; }

    [Path("opc", "LastName")]
    public partial string LastName { get; set; }

    [Derived]
    [Path("opc", "FullName")]
    public string FullName => $"{FirstName} {LastName}";

    [Path("opc", "Scores")]
    public partial double[] Scores { get; set; }

    /// <summary>Deep nesting test - Person.Address.City.</summary>
    [Path("opc", "Address")]
    public partial NestedAddress? Address { get; set; }
}

/// <summary>
/// Address model for deep nesting tests.
/// </summary>
[InterceptorSubject]
public partial class NestedAddress
{
    public NestedAddress()
    {
        City = "";
        ZipCode = "";
    }

    [Path("opc", "City")]
    public partial string City { get; set; }

    [Path("opc", "ZipCode")]
    public partial string ZipCode { get; set; }
}

/// <summary>
/// Sensor model demonstrating OpcUaValue pattern - a VariableNode with child properties.
/// </summary>
[InterceptorSubject]
[OpcUaNode("NestedSensor", NodeClass = OpcUaNodeClass.Variable)]
public partial class NestedSensor
{
    public NestedSensor()
    {
        Value = 0;
        Unit = "";
    }

    /// <summary>The OPC UA Value attribute - this is the VariableNode's value.</summary>
    [OpcUaNode("Value")]
    [OpcUaValue]
    public partial double Value { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("Unit")]
    public partial string? Unit { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("MinValue")]
    public partial double? MinValue { get; set; }

    /// <summary>Child property of the VariableNode.</summary>
    [OpcUaNode("MaxValue")]
    public partial double? MaxValue { get; set; }
}

/// <summary>
/// Test area for various OPC UA data types.
/// </summary>
[InterceptorSubject]
public partial class DataTypesTestArea
{
    public DataTypesTestArea()
    {
        ByteArray = [];
    }

    [Path("opc", "BoolValue")]
    public partial bool BoolValue { get; set; }

    [Path("opc", "IntValue")]
    public partial int IntValue { get; set; }

    [Path("opc", "LongValue")]
    public partial long LongValue { get; set; }

    [Path("opc", "DateTimeValue")]
    public partial DateTime DateTimeValue { get; set; }

    [Path("opc", "ByteArray")]
    public partial byte[] ByteArray { get; set; }
}

/// <summary>
/// Test area for collection edge cases (empty arrays, resize operations).
/// </summary>
[InterceptorSubject]
public partial class CollectionsTestArea
{
    public CollectionsTestArea()
    {
        IntArray = [];
    }

    [Path("opc", "IntArray")]
    public partial int[] IntArray { get; set; }
}

/// <summary>
/// Test area for multi-client scenarios.
/// </summary>
[InterceptorSubject]
public partial class MultiClientTestArea
{
    public MultiClientTestArea()
    {
        SharedValue = "";
    }

    [Path("opc", "SharedValue")]
    public partial string SharedValue { get; set; }

    [Path("opc", "Counter")]
    public partial int Counter { get; set; }

    [Path("opc", "LastWriter")]
    public partial string? LastWriter { get; set; }
}

/// <summary>
/// Test area for client-to-server synchronization tests.
/// Contains nested subjects for testing value sync on existing subjects.
/// </summary>
[InterceptorSubject]
public partial class ClientToServerSyncTestArea
{
    public ClientToServerSyncTestArea()
    {
        Person = new NestedPerson();
        Sensor = new NestedSensor();
    }

    /// <summary>Person reference for testing property value sync on nested objects.</summary>
    [Path("opc", "Person")]
    public partial NestedPerson Person { get; set; }

    /// <summary>OpcUaValue pattern sensor for testing value sync.</summary>
    [OpcUaNode("Sensor")]
    [OpcUaReference("HasComponent")]
    public partial NestedSensor? Sensor { get; set; }
}

/// <summary>
/// Test area for ServerToClientReferenceTests.
/// </summary>
[InterceptorSubject]
public partial class ServerToClientReferenceTestArea
{
    public ServerToClientReferenceTestArea()
    {
        Person = null;
    }

    /// <summary>Reference property for testing assign/clear.</summary>
    [Path("opc", "Person")]
    public partial NestedPerson? Person { get; set; }
}

/// <summary>
/// Test area for ServerToClientCollectionTests.
/// </summary>
[InterceptorSubject]
public partial class ServerToClientCollectionTestArea
{
    public ServerToClientCollectionTestArea()
    {
        ContainerItems = [];
        FlatItems = [];
    }

    /// <summary>Collection with container node (default).</summary>
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] ContainerItems { get; set; }

    /// <summary>Collection without container node (flat).</summary>
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Flat)]
    public partial NestedPerson[] FlatItems { get; set; }
}

/// <summary>
/// Test area for ServerToClientDictionaryTests.
/// </summary>
[InterceptorSubject]
public partial class ServerToClientDictionaryTestArea
{
    public ServerToClientDictionaryTestArea()
    {
        Items = new Dictionary<string, NestedPerson>();
    }

    /// <summary>Dictionary property for testing add/remove.</summary>
    [Path("opc", "Items")]
    public partial Dictionary<string, NestedPerson>? Items { get; set; }
}

/// <summary>
/// Test area for ServerToClientSharedSubjectTests.
/// </summary>
[InterceptorSubject]
public partial class ServerToClientSharedSubjectTestArea
{
    public ServerToClientSharedSubjectTestArea()
    {
        PrimaryItems = [];
        SecondaryItems = [];
    }

    /// <summary>First collection for shared subject testing.</summary>
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] PrimaryItems { get; set; }

    /// <summary>Second collection for shared subject testing.</summary>
    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] SecondaryItems { get; set; }
}

/// <summary>
/// Test area for ClientToServerReferenceTests.
/// </summary>
[InterceptorSubject]
public partial class ClientToServerReferenceTestArea
{
    public ClientToServerReferenceTestArea()
    {
        Person = null;
    }

    [Path("opc", "Person")]
    public partial NestedPerson? Person { get; set; }
}

/// <summary>
/// Test area for ClientToServerCollectionTests.
/// </summary>
[InterceptorSubject]
public partial class ClientToServerCollectionTestArea
{
    public ClientToServerCollectionTestArea()
    {
        ContainerItems = [];
        FlatItems = [];
    }

    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] ContainerItems { get; set; }

    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Flat)]
    public partial NestedPerson[] FlatItems { get; set; }
}

/// <summary>
/// Test area for ClientToServerDictionaryTests.
/// </summary>
[InterceptorSubject]
public partial class ClientToServerDictionaryTestArea
{
    public ClientToServerDictionaryTestArea()
    {
        Items = new Dictionary<string, NestedPerson>();
    }

    [Path("opc", "Items")]
    public partial Dictionary<string, NestedPerson>? Items { get; set; }
}

/// <summary>
/// Test area for ClientToServerSharedSubjectTests.
/// </summary>
[InterceptorSubject]
public partial class ClientToServerSharedSubjectTestArea
{
    public ClientToServerSharedSubjectTestArea()
    {
        PrimaryItems = [];
        SecondaryItems = [];
    }

    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] PrimaryItems { get; set; }

    [OpcUaReference("HasComponent", CollectionStructure = CollectionNodeStructure.Container)]
    public partial NestedPerson[] SecondaryItems { get; set; }
}
