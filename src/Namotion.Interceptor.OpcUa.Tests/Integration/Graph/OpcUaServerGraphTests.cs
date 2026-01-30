using Namotion.Interceptor.OpcUa.Tests.Integration.Testing;
using Namotion.Interceptor.Testing;
using Opc.Ua;
using Xunit.Abstractions;

namespace Namotion.Interceptor.OpcUa.Tests.Integration.Graph;

/// <summary>
/// Tests for OPC UA server graph sync - verifies that model changes update the OPC UA address space in real-time.
/// Tests the Server Model→OPC direction by browsing the server's address space after model changes.
/// </summary>
public class OpcUaServerGraphTests : OpcUaGraphTestBase
{
    public OpcUaServerGraphTests(ITestOutputHelper output) : base(output)
    {
    }

    #region Collection Tests

    [Fact]
    public async Task AddSubjectToCollection_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var peopleNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "People");

        var initialChildren = await BrowseChildNodesAsync(ctx.Session, peopleNodeId);
        Logger.Log($"Initial children count: {initialChildren.Count}");
        Assert.Empty(initialChildren);

        // Act: Add a subject to the collection
        var newPerson = new TestPerson(ctx.Context)
        {
            FirstName = "John",
            LastName = "Doe"
        };
        ctx.Root.People = [newPerson];
        Logger.Log("Added person to collection");

        // Wait for graph sync to propagate
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new node");

        // Verify the new node
        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Assert.Contains("[0]", updatedChildren[0].BrowseName.Name);
        Logger.Log($"Found new child: {updatedChildren[0].BrowseName}");

        // Browse into the new person node and verify properties exist
        var personNodeId = ExpandedNodeId.ToNodeId(updatedChildren[0].NodeId, ctx.Session.NamespaceUris);
        var personChildren = await BrowseChildNodesAsync(ctx.Session, personNodeId);
        Assert.Contains(personChildren, c => c.BrowseName.Name == "FirstName");
        Assert.Contains(personChildren, c => c.BrowseName.Name == "LastName");
        Logger.Log("Verified person node has expected properties");
    }

    [Fact]
    public async Task RemoveSubjectFromCollection_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        // Setup: Add persons to the collection
        var person1 = new TestPerson(ctx.Context) { FirstName = "Alice", LastName = "Smith" };
        var person2 = new TestPerson(ctx.Context) { FirstName = "Bob", LastName = "Jones" };
        ctx.Root.People = [person1, person2];
        Logger.Log("Added two persons to collection");

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var peopleNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "People");

        // Wait for initial sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult().Count == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see two initial nodes");

        // Act: Remove one person
        ctx.Root.People = [person1];
        Logger.Log("Removed Bob from collection");

        // Wait for graph sync to propagate
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the node removed");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Logger.Log($"Remaining child: {updatedChildren[0].BrowseName}");
    }

    [Fact]
    public async Task CollectionItemRemoved_BrowseNamesReindexed()
    {
        await using var ctx = await StartServerWithSessionAsync();

        // Setup: Add three persons to the collection
        var person1 = new TestPerson(ctx.Context) { FirstName = "Alice", LastName = "One" };
        var person2 = new TestPerson(ctx.Context) { FirstName = "Bob", LastName = "Two" };
        var person3 = new TestPerson(ctx.Context) { FirstName = "Charlie", LastName = "Three" };
        ctx.Root.People = [person1, person2, person3];
        Logger.Log("Added three persons to collection");

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var peopleNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "People");

        // Wait for initial sync
        IReadOnlyList<ReferenceDescription>? initialChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                initialChildren = BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult();
                return initialChildren.Count == 3;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see three initial nodes");

        Assert.NotNull(initialChildren);
        Logger.Log($"Initial children: {string.Join(", ", initialChildren.Select(c => c.BrowseName.Name))}");
        Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[0]"));
        Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[1]"));
        Assert.Contains(initialChildren, c => c.BrowseName.Name.Contains("[2]"));

        // Act: Remove the middle person (Bob), keeping Alice and Charlie
        ctx.Root.People = [person1, person3];
        Logger.Log("Removed middle person (Bob) from collection");

        // Wait for graph sync AND re-indexing to complete
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult();
                if (updatedChildren.Count != 2) return false;
                var names = updatedChildren.Select(c => c.BrowseName.Name).ToList();
                Logger.Log($"Checking children: {string.Join(", ", names)}");
                return names.Any(n => n.Contains("[0]")) && names.Any(n => n.Contains("[1]"));
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see two nodes with re-indexed BrowseNames [0] and [1]");

        Assert.NotNull(updatedChildren);
        Logger.Log($"Updated children: {string.Join(", ", updatedChildren.Select(c => c.BrowseName.Name))}");
        Assert.Equal(2, updatedChildren.Count);
        Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[0]"));
        Assert.Contains(updatedChildren, c => c.BrowseName.Name.Contains("[1]"));
        Assert.DoesNotContain(updatedChildren, c => c.BrowseName.Name.Contains("[2]"));
        Logger.Log("BrowseNames were correctly re-indexed");
    }

    [Fact]
    public async Task MultipleAddAndRemove_SequentialOperations()
    {
        await using var ctx = await StartServerWithSessionAsync();

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var peopleNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "People");

        var initialChildren = await BrowseChildNodesAsync(ctx.Session, peopleNodeId);
        Assert.Empty(initialChildren);
        Logger.Log("Verified initial empty state");

        // Add first person
        var person1 = new TestPerson(ctx.Context) { FirstName = "First", LastName = "Person" };
        ctx.Root.People = [person1];

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult().Count == 1,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see first person");
        Logger.Log("First person added");

        // Add second person
        var person2 = new TestPerson(ctx.Context) { FirstName = "Second", LastName = "Person" };
        ctx.Root.People = [person1, person2];

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult().Count == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see second person");
        Logger.Log("Second person added");

        // Remove first person
        ctx.Root.People = [person2];

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult();
                return children.Count == 1 && children[0].BrowseName.Name.Contains("[0]");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see one person with re-indexed BrowseName");
        Logger.Log("First person removed, second person re-indexed to [0]");

        // Clear all
        ctx.Root.People = [];

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, peopleNodeId).GetAwaiter().GetResult().Count == 0,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see empty collection");
        Logger.Log("Collection cleared");
    }

    #endregion

    #region Reference Tests

    [Fact]
    public async Task ReplaceSubjectReference_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        // Setup: Add initial person reference
        var originalPerson = new TestPerson(ctx.Context)
        {
            FirstName = "Original",
            LastName = "Person"
        };
        ctx.Root.Person = originalPerson;
        Logger.Log("Set initial Person reference");

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);

        // Wait for Person node to appear
        NodeId personNodeId = NodeId.Null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var rootChildren = BrowseChildNodesAsync(ctx.Session, rootNodeId).GetAwaiter().GetResult();
                var personRef = rootChildren.FirstOrDefault(c => c.BrowseName.Name == "Person");
                if (personRef != null)
                {
                    personNodeId = ExpandedNodeId.ToNodeId(personRef.NodeId, ctx.Session.NamespaceUris);
                    return true;
                }
                return false;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see initial Person node");

        Assert.NotEqual(NodeId.Null, personNodeId);
        Logger.Log($"Found initial Person node: {personNodeId}");

        // Verify original FirstName value
        var personChildren = await BrowseChildNodesAsync(ctx.Session, personNodeId);
        var firstNameRef = personChildren.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
        Assert.NotNull(firstNameRef);
        var originalFirstNameNodeId = ExpandedNodeId.ToNodeId(firstNameRef.NodeId, ctx.Session.NamespaceUris);
        var originalFirstNameValue = await ReadValueAsync(ctx.Session, originalFirstNameNodeId);
        Assert.Equal("Original", originalFirstNameValue.Value);
        Logger.Log($"Original FirstName: {originalFirstNameValue.Value}");

        // Act: Replace with a new person
        var newPerson = new TestPerson(ctx.Context)
        {
            FirstName = "Replacement",
            LastName = "Person"
        };
        ctx.Root.Person = newPerson;
        Logger.Log("Replaced Person reference");

        // Wait for graph sync to propagate the new value
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var rootChildrenNow = BrowseChildNodesAsync(ctx.Session, rootNodeId).GetAwaiter().GetResult();
                var personRefNow = rootChildrenNow.FirstOrDefault(c => c.BrowseName.Name == "Person");
                if (personRefNow == null) return false;

                var personNodeIdNow = ExpandedNodeId.ToNodeId(personRefNow.NodeId, ctx.Session.NamespaceUris);
                var personChildrenNow = BrowseChildNodesAsync(ctx.Session, personNodeIdNow).GetAwaiter().GetResult();
                var firstNameRefNow = personChildrenNow.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
                if (firstNameRefNow == null) return false;

                var firstNameNodeIdNow = ExpandedNodeId.ToNodeId(firstNameRefNow.NodeId, ctx.Session.NamespaceUris);
                var value = ReadValueAsync(ctx.Session, firstNameNodeIdNow).GetAwaiter().GetResult();
                return value.Value?.ToString() == "Replacement";
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new Person's FirstName value");

        Logger.Log("Verified reference replacement");
    }

    [Fact]
    public async Task AssignSubjectReference_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);

        // Verify Person node doesn't exist initially (null reference)
        var rootChildren = await BrowseChildNodesAsync(ctx.Session, rootNodeId);
        Assert.DoesNotContain(rootChildren, c => c.BrowseName.Name == "Person");
        Logger.Log("Verified Person node doesn't exist initially");

        // Act: Assign a person reference
        var newPerson = new TestPerson(ctx.Context)
        {
            FirstName = "New",
            LastName = "Person"
        };
        ctx.Root.Person = newPerson;
        Logger.Log("Assigned Person reference");

        // Wait for graph sync to create the node
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.Session, rootNodeId).GetAwaiter().GetResult();
                return children.Any(c => c.BrowseName.Name == "Person");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see Person node created");

        Logger.Log("Verified Person node was created");
    }

    [Fact]
    public async Task ClearSubjectReference_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        // Setup: Assign initial person reference
        var person = new TestPerson(ctx.Context) { FirstName = "ToClear", LastName = "Person" };
        ctx.Root.Person = person;
        Logger.Log("Assigned initial Person reference");

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);

        // Wait for Person node to appear
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.Session, rootNodeId).GetAwaiter().GetResult();
                return children.Any(c => c.BrowseName.Name == "Person");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see initial Person node");

        Logger.Log("Verified Person node exists");

        // Act: Clear the reference
        ctx.Root.Person = null!;
        Logger.Log("Cleared Person reference");

        // Wait for graph sync to remove the node
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.Session, rootNodeId).GetAwaiter().GetResult();
                return !children.Any(c => c.BrowseName.Name == "Person");
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see Person node removed");

        Logger.Log("Verified Person node was removed");
    }

    #endregion

    #region Dictionary Tests

    [Fact]
    public async Task AddToDictionary_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var dictNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "PeopleByName");

        var initialChildren = await BrowseChildNodesAsync(ctx.Session, dictNodeId);
        Logger.Log($"Initial dictionary children count: {initialChildren.Count}");
        Assert.Empty(initialChildren);

        // Act: Add to dictionary
        var newPerson = new TestPerson(ctx.Context)
        {
            FirstName = "Dict",
            LastName = "Entry"
        };
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["mykey"] = newPerson
        };
        Logger.Log("Added 'mykey' to dictionary");

        // Wait for graph sync to propagate
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see the new dictionary entry");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Assert.Equal("mykey", updatedChildren[0].BrowseName.Name);
        Logger.Log($"Found dictionary entry: {updatedChildren[0].BrowseName}");

        // Verify the entry has expected properties
        var entryNodeId = ExpandedNodeId.ToNodeId(updatedChildren[0].NodeId, ctx.Session.NamespaceUris);
        var entryChildren = await BrowseChildNodesAsync(ctx.Session, entryNodeId);
        Assert.Contains(entryChildren, c => c.BrowseName.Name == "FirstName");
        Assert.Contains(entryChildren, c => c.BrowseName.Name == "LastName");
        Logger.Log("Verified dictionary entry has expected properties");
    }

    [Fact]
    public async Task RemoveFromDictionary_ClientSeesBrowseChange()
    {
        await using var ctx = await StartServerWithSessionAsync();

        // Setup: Add entries to dictionary
        var person1 = new TestPerson(ctx.Context) { FirstName = "Keep", LastName = "Entry" };
        var person2 = new TestPerson(ctx.Context) { FirstName = "Remove", LastName = "Entry" };
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1,
            ["remove"] = person2
        };
        Logger.Log("Added two dictionary entries");

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var dictNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "PeopleByName");

        // Wait for initial sync
        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult().Count == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see two initial dictionary entries");

        Logger.Log("Verified two initial entries");

        // Act: Remove one entry
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["keep"] = person1
        };
        Logger.Log("Removed 'remove' key from dictionary");

        // Wait for graph sync to propagate
        IReadOnlyList<ReferenceDescription>? updatedChildren = null;
        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                updatedChildren = BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult();
                return updatedChildren.Count == 1;
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see entry removed");

        Assert.NotNull(updatedChildren);
        Assert.Single(updatedChildren);
        Assert.Equal("keep", updatedChildren[0].BrowseName.Name);
        Logger.Log("Verified dictionary now has only 'keep' entry");
    }

    [Fact]
    public async Task MultipleDictionaryOperations_SequentialChanges()
    {
        await using var ctx = await StartServerWithSessionAsync();

        var rootNodeId = await FindRootNodeIdAsync(ctx.Session);
        var dictNodeId = await FindChildNodeIdAsync(ctx.Session, rootNodeId, "PeopleByName");

        // Verify empty initially
        var initialChildren = await BrowseChildNodesAsync(ctx.Session, dictNodeId);
        Assert.Empty(initialChildren);
        Logger.Log("Verified initial empty dictionary");

        // Add first entry
        var person1 = new TestPerson(ctx.Context) { FirstName = "First", LastName = "Entry" };
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson> { ["first"] = person1 };

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult().Count == 1,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see first entry");
        Logger.Log("First entry added");

        // Add second entry (keep first)
        var person2 = new TestPerson(ctx.Context) { FirstName = "Second", LastName = "Entry" };
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["first"] = person1,
            ["second"] = person2
        };

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult().Count == 2,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see second entry");
        Logger.Log("Second entry added");

        // Replace value for existing key
        var person1Replacement = new TestPerson(ctx.Context) { FirstName = "Replaced", LastName = "Entry" };
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>
        {
            ["first"] = person1Replacement,
            ["second"] = person2
        };

        await AsyncTestHelpers.WaitUntilAsync(
            () =>
            {
                var children = BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult();
                if (children.Count != 2) return false;

                var firstEntry = children.FirstOrDefault(c => c.BrowseName.Name == "first");
                if (firstEntry == null) return false;

                var entryNodeId = ExpandedNodeId.ToNodeId(firstEntry.NodeId, ctx.Session.NamespaceUris);
                var entryChildren = BrowseChildNodesAsync(ctx.Session, entryNodeId).GetAwaiter().GetResult();
                var firstNameRef = entryChildren.FirstOrDefault(c => c.BrowseName.Name == "FirstName");
                if (firstNameRef == null) return false;

                var firstNameNodeId = ExpandedNodeId.ToNodeId(firstNameRef.NodeId, ctx.Session.NamespaceUris);
                var value = ReadValueAsync(ctx.Session, firstNameNodeId).GetAwaiter().GetResult();
                return value.Value?.ToString() == "Replaced";
            },
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see replaced entry value");
        Logger.Log("Entry value replaced");

        // Clear all
        ctx.Root.PeopleByName = new Dictionary<string, TestPerson>();

        await AsyncTestHelpers.WaitUntilAsync(
            () => BrowseChildNodesAsync(ctx.Session, dictNodeId).GetAwaiter().GetResult().Count == 0,
            timeout: TimeSpan.FromSeconds(10),
            message: "Client should see empty dictionary");
        Logger.Log("Dictionary cleared");
    }

    #endregion
}
