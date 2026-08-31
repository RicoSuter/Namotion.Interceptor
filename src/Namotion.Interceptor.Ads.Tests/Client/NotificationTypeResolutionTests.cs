using Moq;
using Namotion.Interceptor.Ads.Client;
using TwinCAT.TypeSystem;
using Xunit;

namespace Namotion.Interceptor.Ads.Tests.Client;

/// <summary>
/// The ADS any-type marshaller cannot take a property's declared type as-is: an enum and a nullable
/// are rejected outright, and a string or array needs a length only the PLC symbol knows.
/// </summary>
public class NotificationTypeResolutionTests
{
    private enum Mode : short
    {
        Idle = 0,
        Running = 1,
    }

    private static ISymbol CreateSymbol(int byteSize)
    {
        var symbol = new Mock<ISymbol>();
        symbol.As<IBitSize>().Setup(b => b.ByteSize).Returns(byteSize);
        return symbol.Object;
    }

    [Theory]
    [InlineData(typeof(bool), 1)]
    [InlineData(typeof(short), 2)]
    [InlineData(typeof(int), 4)]
    [InlineData(typeof(long), 8)]
    [InlineData(typeof(float), 4)]
    [InlineData(typeof(double), 8)]
    public void Resolve_ForAPrimitiveMatchingThePlcWidth_Succeeds(Type propertyType, int byteSize)
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            propertyType, CreateSymbol(byteSize), out var marshalType, out var args));
        Assert.Equal(propertyType, marshalType);
        Assert.Null(args);
    }

    [Fact]
    public void Resolve_ForANullable_UnwrapsIt()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(double?), CreateSymbol(8), out var marshalType, out _));
        Assert.Equal(typeof(double), marshalType);
    }

    [Fact]
    public void Resolve_ForAnEnum_UsesItsUnderlyingType()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(Mode), CreateSymbol(2), out var marshalType, out _));
        Assert.Equal(typeof(short), marshalType);
    }

    [Fact]
    public void Resolve_ForAString_TakesTheLengthFromThePlcSymbol()
    {
        // A PLC STRING(80) occupies 81 bytes, and the marshaller wants the 80.
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(string), CreateSymbol(81), out var marshalType, out var args));
        Assert.Equal(typeof(string), marshalType);
        Assert.Equal([80], Assert.IsType<int[]>(args));
    }

    [Fact]
    public void Resolve_ForAnArray_DerivesTheElementCountFromThePlcSymbol()
    {
        Assert.True(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(int[]), CreateSymbol(20), out var marshalType, out var args));
        Assert.Equal(typeof(int[]), marshalType);
        Assert.Equal([5], Assert.IsType<int[]>(args));
    }

    [Theory]
    [InlineData(typeof(float), 8)]  // REAL property over an LREAL symbol
    [InlineData(typeof(long), 4)]   // LINT property over a DINT symbol
    public void Resolve_WhenTheWidthDisagreesWithThePlc_Fails(Type propertyType, int byteSize)
    {
        // A narrower type is refused by the controller anyway; a wider one is accepted and reads
        // past the variable, so neither may be registered.
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            propertyType, CreateSymbol(byteSize), out _, out _));
    }

    [Fact]
    public void Resolve_ForATypeTheMarshallerRejects_Fails()
    {
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(char), CreateSymbol(2), out _, out _));
    }

    [Fact]
    public void Resolve_WhenTheSymbolHasNoSize_Fails()
    {
        Assert.False(AdsSubscriptionManager.TryResolveNotificationType(
            typeof(int), Mock.Of<ISymbol>(), out _, out _));
    }
}
