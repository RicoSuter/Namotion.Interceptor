using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Attributes;
using Namotion.Interceptor.Modbus.Mapping;

namespace Namotion.Interceptor.Modbus.Tests.Mapping;

public partial class ModbusReadPlannerTests
{
    [InterceptorSubject]
    public partial class PlannerSubject
    {
        public partial int? Value { get; set; }
    }

    private static readonly PlannerSubject Subject = new();

    private static ModbusRegisterBinding CreateBinding(
        int address, ModbusDataType dataType = ModbusDataType.U16,
        ModbusAddressSpace space = ModbusAddressSpace.HoldingRegister, byte unitId = 1, int length = 0)
        => new(new PropertyReference(Subject, nameof(PlannerSubject.Value)), $"Value{address}", unitId, address,
            new ModbusRegisterAttribute(address, dataType) { AddressSpace = space, Length = length }, static (_, _) => null);

    [Fact]
    public void WhenRegistersAreContiguous_ThenOneBatchCoversThem()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(1), CreateBinding(2, ModbusDataType.U32)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 4), (batch.StartAddress, batch.Count));
        Assert.Equal(3, batch.Bindings.Length);
    }

    [Fact]
    public void WhenGapIsNotAllowed_ThenGapSplitsTheBatch()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(2)], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 1), (2, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenGapIsWithinTolerance_ThenBatchSpansTheGap()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0), CreateBinding(2)], maximumGap: 1);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 3), (batch.StartAddress, batch.Count));
    }

    [Fact]
    public void WhenRegistersExceedTheRequestLimit_ThenBatchesAreSplitAt125()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(Enumerable.Range(0, 130).Select(address => CreateBinding(address)), maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 125), (125, 5) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenBitsExceedTheRequestLimit_ThenBatchesAreSplitAt2000()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            Enumerable.Range(0, 2005).Select(address => CreateBinding(address, ModbusDataType.Boolean, ModbusAddressSpace.Coil)),
            maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 2000), (2000, 5) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenUnitsOrSpacesDiffer_ThenBatchesAreSeparate()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            [CreateBinding(0), CreateBinding(1, unitId: 2), CreateBinding(1, space: ModbusAddressSpace.InputRegister)],
            maximumGap: 10);

        // Assert
        Assert.Equal(3, batches.Length);
    }

    [Fact]
    public void WhenMappingsOverlap_ThenTheyShareOneBatch()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.String, length: 4), CreateBinding(2)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 4), (batch.StartAddress, batch.Count));
    }

    [Fact]
    public void WhenBindingsAreIsolated_ThenEachIsReadAloneAndOthersStillMerge()
    {
        // Arrange
        var first = CreateBinding(0);
        var second = CreateBinding(1);
        first.IsIsolated = true;
        second.IsIsolated = true;

        // Act
        var batches = ModbusReadPlanner.Plan([first, second, CreateBinding(2), CreateBinding(3)], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 1), (1, 1), (2, 2) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenRangesAreIdentical_ThenTheyShareOneBatchOfTheirSize()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.U32), CreateBinding(0, ModbusDataType.U32)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 2), (batch.StartAddress, batch.Count));
        Assert.Equal(2, batch.Bindings.Length);
    }

    [Fact]
    public void WhenNarrowViewSharesTheStartOfAWideView_ThenOneBatchCoversTheWideView()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.U32), CreateBinding(0)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 2), (batch.StartAddress, batch.Count));
        Assert.Equal(2, batch.Bindings.Length);
    }

    [Fact]
    public void WhenBindingIsContainedInAnother_ThenTheBatchEndIsNotShrunk()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            [CreateBinding(0, ModbusDataType.String, length: 4), CreateBinding(1), CreateBinding(4)],
            maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 5), (batch.StartAddress, batch.Count));
        Assert.Equal(3, batch.Bindings.Length);
    }

    [Fact]
    public void WhenBindingIsContainedInAnotherAndGapFollows_ThenGapIsMeasuredFromTheWiderEnd()
    {
        // Act
        var batches = ModbusReadPlanner.Plan(
            [CreateBinding(0, ModbusDataType.String, length: 4), CreateBinding(1), CreateBinding(6)],
            maximumGap: 1);

        // Assert
        Assert.Equal(new[] { (0, 4), (6, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenOverlappingBindingWouldExceedTheRequestLimit_ThenItStartsANewBatch()
    {
        // Arrange
        var bindings = Enumerable.Range(0, 124).Select(address => CreateBinding(address))
            .Append(CreateBinding(123, ModbusDataType.U32))
            .Append(CreateBinding(124, ModbusDataType.U32));

        // Act
        var batches = ModbusReadPlanner.Plan(bindings, maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 125), (124, 2) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
        Assert.Equal(125, batches[0].Bindings.Length);
    }

    [Fact]
    public void WhenOverlappingBindingIsIsolated_ThenItIsReadAlone()
    {
        // Arrange
        var isolated = CreateBinding(1);
        isolated.IsIsolated = true;

        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.U32), isolated, CreateBinding(0)], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 2), (1, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
        Assert.Same(isolated, Assert.Single(batches[1].Bindings));
    }

    [Fact]
    public void WhenIdenticalRangesAreIsolated_ThenEachIsReadAlone()
    {
        // Arrange
        var first = CreateBinding(0);
        var second = CreateBinding(0);
        first.IsIsolated = true;
        second.IsIsolated = true;

        // Act
        var batches = ModbusReadPlanner.Plan([first, second], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 1), (0, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
    }

    [Fact]
    public void WhenIsolatedBindingSharesTheKeyOfAMergeableBinding_ThenTheMergeableBindingStillMerges()
    {
        // Arrange
        var first = CreateBinding(0);
        var isolated = CreateBinding(1);
        var second = CreateBinding(1);
        isolated.IsIsolated = true;

        // Act
        var batches = ModbusReadPlanner.Plan([first, isolated, second], maximumGap: 0);

        // Assert
        Assert.Equal(new[] { (0, 2), (1, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
        Assert.Equal(new[] { first, second }, batches[0].Bindings);
        Assert.Same(isolated, Assert.Single(batches[1].Bindings));
    }

    [Theory]
    [InlineData(126, new[] { 10, 125, 135, 1 })]
    [InlineData(150, new[] { 10, 125, 135, 25 })]
    [InlineData(250, new[] { 10, 125, 135, 125 })]
    [InlineData(300, new[] { 10, 125, 135, 125, 260, 50 })]
    public void WhenStringIsLongerThanOneRequest_ThenItIsReadAloneInConsecutiveRequests(int length, int[] expectedRequests)
    {
        // Arrange
        var longString = CreateBinding(10, ModbusDataType.String, length: length);
        var bindings = new[] { CreateBinding(9), longString, CreateBinding(20), CreateBinding(10 + length) };

        // Act
        var batches = ModbusReadPlanner.Plan(bindings, maximumGap: 10);

        // Assert
        Assert.Equal(new[] { (9, 1), (10, length), (20, 1), (10 + length, 1) }, batches.Select(batch => (batch.StartAddress, batch.Count)));
        var batch = batches[1];
        Assert.Same(longString, Assert.Single(batch.Bindings));
        var requests = Enumerable.Range(0, batch.RequestCount).Select(batch.GetRequest);
        Assert.Equal(expectedRequests.Chunk(2).Select(request => (request[0], request[1])), requests);
        Assert.All(batches.Where(other => other != batch), other => Assert.Equal(1, other.RequestCount));
    }

    [Fact]
    public void WhenStringFillsOneRequest_ThenItIsMergedAsUsual()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([CreateBinding(0, ModbusDataType.String, length: 124), CreateBinding(124)], maximumGap: 0);

        // Assert
        var batch = Assert.Single(batches);
        Assert.Equal((0, 125, 1), (batch.StartAddress, batch.Count, batch.RequestCount));
    }

    [Fact]
    public void WhenThereAreNoBindings_ThenNoBatchesArePlanned()
    {
        // Act
        var batches = ModbusReadPlanner.Plan([], maximumGap: 0);

        // Assert
        Assert.Empty(batches);
    }
}
