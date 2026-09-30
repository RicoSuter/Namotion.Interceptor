using Namotion.Interceptor.Cache;

namespace Namotion.Interceptor.Tests;

public class AtomicAccessTests
{
    public enum IntegerBacked
    {
        First
    }

    public enum LongBacked : long
    {
        First
    }

    public readonly record struct IntegerWrapper(int Value);

    [Theory]
    [InlineData(typeof(string))]
    [InlineData(typeof(object))]
    [InlineData(typeof(int[]))]
    [InlineData(typeof(bool))]
    [InlineData(typeof(byte))]
    [InlineData(typeof(char))]
    [InlineData(typeof(short))]
    [InlineData(typeof(int))]
    [InlineData(typeof(uint))]
    [InlineData(typeof(float))]
    [InlineData(typeof(IntPtr))]
    [InlineData(typeof(UIntPtr))]
    [InlineData(typeof(IntegerBacked))]
    public void WhenTypeIsReferenceOrWordSizedPrimitive_ThenAccessIsAtomic(Type type)
    {
        // Act
        var isAtomic = AtomicAccess.IsGuaranteedFor(type);

        // Assert
        Assert.True(isAtomic);
    }

    [Theory]
    [InlineData(typeof(long))]
    [InlineData(typeof(ulong))]
    [InlineData(typeof(double))]
    [InlineData(typeof(LongBacked))]
    public void WhenTypeIsEightBytesWide_ThenAccessIsAtomicOnlyOnSixtyFourBitRuntime(Type type)
    {
        // Act
        var isAtomic = AtomicAccess.IsGuaranteedFor(type);

        // Assert
        Assert.Equal(Environment.Is64BitProcess, isAtomic);
    }

    [Theory]
    [InlineData(typeof(decimal))]
    [InlineData(typeof(DateTime))]
    [InlineData(typeof(DateTimeOffset))]
    [InlineData(typeof(TimeSpan))]
    [InlineData(typeof(Guid))]
    [InlineData(typeof(int?))]
    [InlineData(typeof(IntegerWrapper))]
    [InlineData(typeof(WideValue))]
    public void WhenTypeIsValueTypeWithoutRuntimeGuarantee_ThenAccessIsNotAtomic(Type type)
    {
        // Act
        var isAtomic = AtomicAccess.IsGuaranteedFor(type);

        // Assert
        Assert.False(isAtomic);
    }
}
