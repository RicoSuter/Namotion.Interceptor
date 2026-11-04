using Namotion.Devices.SunSpec.Definitions;
using Namotion.Devices.SunSpec.Models;
using Namotion.Interceptor.Modbus;

namespace Namotion.Devices.SunSpec.Tests.Models;

public class SunSpecGroupsTests
{
    private class TestGroup(int baseAddress) : IModbusBaseAddressProvider
    {
        public int BaseAddress { get; } = baseAddress;
    }

    private sealed class TestOwnerGroup(int baseAddress) : TestGroup(baseAddress), ISunSpecGroupOwner
    {
        public List<SunSpecGroupInstance> Updates { get; } = [];

        public void UpdateGroups(SunSpecGroupInstance instance) => Updates.Add(instance);
    }

    private static SunSpecGroupInstance Instance(int address, int index) => new()
    {
        Definition = new SunSpecGroupDefinition { Name = "g" },
        Address = address,
        Index = index,
        Length = 1,
        Groups = new Dictionary<string, IReadOnlyList<SunSpecGroupInstance>>()
    };

    [Fact]
    public void WhenInstancesAreUnchanged_ThenTheCurrentArrayIsReturned()
    {
        // Arrange
        TestGroup[] current = [new(10), new(11)];

        // Act
        var result = SunSpecGroups.Update(current, [Instance(10, 0), Instance(11, 1)], instance => new TestGroup(instance.Address));

        // Assert
        Assert.Same(current, result);
    }

    [Fact]
    public void WhenAnInstanceIsAdded_ThenExistingSubjectsAreKept()
    {
        // Arrange
        TestGroup[] current = [new(10)];

        // Act
        var result = SunSpecGroups.Update(current, [Instance(10, 0), Instance(11, 1)], instance => new TestGroup(instance.Address));

        // Assert
        Assert.NotSame(current, result);
        Assert.Same(current[0], result[0]);
        Assert.Equal(11, result[1].BaseAddress);
    }

    [Fact]
    public void WhenAnInstanceIsRemoved_ThenANewArrayWithTheRemainingSubjectsIsReturned()
    {
        // Arrange
        TestGroup[] current = [new(10), new(11)];

        // Act
        var result = SunSpecGroups.Update(current, [Instance(10, 0)], instance => new TestGroup(instance.Address));

        // Assert
        Assert.NotSame(current, result);
        Assert.Same(current[0], Assert.Single(result));
    }

    [Fact]
    public void WhenAGroupIsAnOwner_ThenItsNestedGroupsAreUpdated()
    {
        // Arrange
        var existing = new TestOwnerGroup(10);
        TestGroup[] current = [existing];
        var instance = Instance(10, 0);

        // Act
        SunSpecGroups.Update(current, [instance], created => new TestOwnerGroup(created.Address));

        // Assert
        Assert.Same(instance, Assert.Single(existing.Updates));
    }

    [Fact]
    public void WhenSingleGroupMoves_ThenANewSubjectIsCreated()
    {
        // Arrange
        var current = new TestGroup(10);

        // Act
        var result = SunSpecGroups.UpdateSingle(current, [Instance(20, 0)], instance => new TestGroup(instance.Address));

        // Assert
        Assert.NotSame(current, result);
        Assert.Equal(20, result!.BaseAddress);
    }

    [Fact]
    public void WhenSingleGroupIsUnchanged_ThenTheCurrentSubjectIsReturned()
    {
        // Arrange
        var current = new TestGroup(10);

        // Act
        var result = SunSpecGroups.UpdateSingle(current, [Instance(10, 0)], instance => new TestGroup(instance.Address));

        // Assert
        Assert.Same(current, result);
    }

    [Fact]
    public void WhenSingleGroupHasNoInstance_ThenNullIsReturned()
    {
        // Arrange
        var current = new TestGroup(10);

        // Act
        var result = SunSpecGroups.UpdateSingle(current, [], instance => new TestGroup(instance.Address));

        // Assert
        Assert.Null(result);
    }
}
