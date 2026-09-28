using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Modbus.Tests.Testing;

namespace Namotion.Interceptor.Modbus.Tests;

public partial class ModbusDiscoveryContextTests
{
    [InterceptorSubject]
    public partial class ContextSubject
    {
        public partial int? Value { get; set; }
    }

    private static ModbusDiscoveryContext Create(FakeRegisterReader reader) => new(source: null!, reader, defaultUnitId: 1);

    [Fact]
    public async Task WhenReadingHoldingRegisters_ThenValuesAreDecodedWithTheDefaultUnit()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetRegister(400, 3);
        reader.SetRegister(401, 92);
        var context = Create(reader);

        // Act
        var registers = await context.ReadHoldingRegistersAsync(400, 2, CancellationToken.None);

        // Assert
        Assert.Equal(new ushort[] { 3, 92 }, registers);
        Assert.Equal((byte)1, Assert.Single(reader.Requests).UnitId);
    }

    [Fact]
    public async Task WhenUnitIdIsGiven_ThenItIsUsed()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetRegister(0, 5, ModbusAddressSpace.InputRegister, unitId: 4);
        var context = Create(reader);

        // Act
        var registers = await context.ReadInputRegistersAsync(0, 1, CancellationToken.None, unitId: 4);

        // Assert
        Assert.Equal(new ushort[] { 5 }, registers);
    }

    [Fact]
    public async Task WhenReadingDiscreteInputs_ThenBitsAreDecoded()
    {
        // Arrange
        var reader = new FakeRegisterReader();
        reader.SetBit(10001, true, ModbusAddressSpace.DiscreteInput);
        var context = Create(reader);

        // Act
        var bits = await context.ReadDiscreteInputsAsync(10000, 3, CancellationToken.None);

        // Assert
        Assert.Equal(new[] { false, true, false }, bits);
    }

    [Fact]
    public void WhenExcludingProperty_ThenItIsInTheExcludedSet()
    {
        // Arrange
        var context = Create(new FakeRegisterReader());
        var property = new PropertyReference(new ContextSubject(), nameof(ContextSubject.Value));

        // Act
        context.ExcludeProperty(property);

        // Assert
        Assert.Contains(property, context.ExcludedProperties);
    }

    [Fact]
    public async Task WhenInvalidated_ThenReadsAndExclusionsThrow()
    {
        // Arrange
        var context = Create(new FakeRegisterReader());
        context.Invalidate();

        // Act & Assert
        await Assert.ThrowsAsync<ObjectDisposedException>(() => context.ReadHoldingRegistersAsync(0, 1, CancellationToken.None));
        Assert.Throws<ObjectDisposedException>(() =>
            context.ExcludeProperty(new PropertyReference(new ContextSubject(), nameof(ContextSubject.Value))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(126)]
    public async Task WhenRegisterCountIsOutOfRange_ThenArgumentOutOfRangeExceptionIsThrown(int count)
    {
        // Arrange
        var context = Create(new FakeRegisterReader());

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => context.ReadHoldingRegistersAsync(0, count, CancellationToken.None));
    }
}
