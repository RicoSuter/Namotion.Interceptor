using Namotion.Interceptor.Tracking.Change.Performance;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class InlineValueStorageTests
{
    [Fact]
    public void WhenBoxingStoredValue_ThenEveryInlineTypeBoxesAsItself()
    {
        AssertBoxesAsItself(42);
        AssertBoxesAsItself(42L);
        AssertBoxesAsItself(4.2);
        AssertBoxesAsItself(4.2f);
        AssertBoxesAsItself(true);
        AssertBoxesAsItself((byte)42);
        AssertBoxesAsItself((sbyte)-42);
        AssertBoxesAsItself((short)-42);
        AssertBoxesAsItself((ushort)42);
        AssertBoxesAsItself(42u);
        AssertBoxesAsItself(42ul);
        AssertBoxesAsItself('x');
        AssertBoxesAsItself((nint)42);
        AssertBoxesAsItself(4.2m);
        AssertBoxesAsItself(new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        AssertBoxesAsItself(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)));
        AssertBoxesAsItself(TimeSpan.FromSeconds(42));
        AssertBoxesAsItself(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        AssertBoxesAsItself(DayOfWeek.Friday);
        AssertBoxesAsItself(new SmallStruct(4, 2));
        AssertBoxesAsItself<int?>(42);
        AssertBoxesAsItself<int?>(null);
    }

    [Fact]
    public void WhenReadingWithAnotherType_ThenNoValueIsReturned()
    {
        // Arrange
        var storage = InlineValueStorage.Create(DayOfWeek.Friday);

        // Act
        var readAsUnderlyingType = storage.TryGetValue<int>(out _);
        var readAsStoredType = storage.TryGetValue<DayOfWeek>(out var value);
        var readAsObject = storage.TryGetValue<object>(out _);

        // Assert
        Assert.False(readAsUnderlyingType);
        Assert.True(readAsStoredType);
        Assert.Equal(DayOfWeek.Friday, value);
        Assert.False(readAsObject);
    }

    [Fact]
    public void WhenNothingIsStored_ThenNoValueIsReturned()
    {
        // Arrange
        var storage = default(InlineValueStorage);

        // Act
        var boxed = storage.GetValueBoxed();
        var readAsString = storage.TryGetValue<string>(out var stringValue);
        var readAsLargeStruct = storage.TryGetValue<LargeStruct>(out var largeStructValue);
        var readAsStructWithReference = storage.TryGetValue<StructWithReference>(out var structWithReferenceValue);
        var readAsInt = storage.TryGetValue<int>(out var intValue);

        // Assert
        Assert.True(storage.IsEmpty);
        Assert.Null(storage.StoredType);
        Assert.Null(boxed);
        Assert.False(readAsString);
        Assert.Null(stringValue);
        Assert.False(readAsLargeStruct);
        Assert.Equal(default, largeStructValue);
        Assert.False(readAsStructWithReference);
        Assert.Equal(default, structWithReferenceValue);
        Assert.False(readAsInt);
        Assert.Equal(0, intValue);
    }

    private static void AssertBoxesAsItself<TValue>(TValue value)
    {
        // Arrange
        var storage = InlineValueStorage.Create(value);

        // Act
        var boxed = storage.GetValueBoxed();

        // Assert
        Assert.Equal(value, boxed);
        Assert.Equal(value?.GetType(), boxed?.GetType());
        Assert.Equal(typeof(TValue), storage.StoredType);
    }

    private readonly record struct SmallStruct(int First, int Second);

    private readonly record struct LargeStruct(long First, long Second, long Third);

    private readonly record struct StructWithReference(int Number, string? Text);
}
