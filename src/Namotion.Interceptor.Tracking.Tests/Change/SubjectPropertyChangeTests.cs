using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Change;

public class SubjectPropertyChangeTests
{
    private readonly PropertyReference _property;
    private readonly DateTimeOffset _changedTimestamp = DateTimeOffset.UtcNow;
    private readonly DateTimeOffset _receivedTimestamp = DateTimeOffset.UtcNow.AddMilliseconds(-10);

    public SubjectPropertyChangeTests()
    {
        var context = InterceptorSubjectContext.Create().WithFullPropertyTracking();
        var person = new Person(context);
        _property = new PropertyReference(person, nameof(Person.FirstName));
    }

    [Theory]
    [InlineData("OldName", "NewName")]
    [InlineData("", "NewName")]
    [InlineData("Test", "")]
    public void Create_WithString_StoresAndRetrievesCorrectly(string oldValue, string newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void Create_WithNullString_StoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        const string newValue = "NewName";

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void Create_WithBothStringsNull_StoresAndRetrievesCorrectly()
    {
        // Arrange
        string? oldValue = null;
        string? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<string>());
        Assert.Null(change.GetNewValue<string>());
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(0, -1)]
    public void Create_WithInt_StoresAndRetrievesCorrectly(int oldValue, int newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int>());
        Assert.Equal(newValue, change.GetNewValue<int>());
    }

    [Theory]
    [InlineData(123456789012345L, 987654321098765L)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void Create_WithLong_StoresAndRetrievesCorrectly(long oldValue, long newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<long>());
        Assert.Equal(newValue, change.GetNewValue<long>());
    }

    [Theory]
    [InlineData(3.14159265358979, 2.71828182845904)]
    [InlineData(double.MinValue, double.MaxValue)]
    public void Create_WithDouble_StoresAndRetrievesCorrectly(double oldValue, double newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<double>());
        Assert.Equal(newValue, change.GetNewValue<double>());
    }

    [Theory]
    [InlineData(3.14f, 2.71f)]
    [InlineData(float.MinValue, float.MaxValue)]
    public void Create_WithFloat_StoresAndRetrievesCorrectly(float oldValue, float newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<float>());
        Assert.Equal(newValue, change.GetNewValue<float>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Create_WithBool_StoresAndRetrievesCorrectly(bool oldValue, bool newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<bool>());
        Assert.Equal(newValue, change.GetNewValue<bool>());
    }

    [Theory]
    [InlineData((byte)0, (byte)255)]
    [InlineData((byte)128, (byte)64)]
    public void Create_WithByte_StoresAndRetrievesCorrectly(byte oldValue, byte newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<byte>());
        Assert.Equal(newValue, change.GetNewValue<byte>());
    }

    [Theory]
    [InlineData('A', 'Z')]
    [InlineData('0', '9')]
    public void Create_WithChar_StoresAndRetrievesCorrectly(char oldValue, char newValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<char>());
        Assert.Equal(newValue, change.GetNewValue<char>());
    }

    public static IEnumerable<object[]> LargerValueTypeTestData()
    {
        yield return [123456789.123456789m, 987654321.987654321m];
        yield return [new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc)];
        yield return [new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5))];
        yield return [Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")];
        yield return [TimeSpan.FromHours(1.5), TimeSpan.FromDays(7)];
    }

    [Theory]
    [MemberData(nameof(LargerValueTypeTestData))]
    public void Create_WithLargerValueTypes_StoresAndRetrievesCorrectly(object oldValue, object newValue)
    {
        // Arrange
        var method = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.Create))!
            .MakeGenericMethod(oldValue.GetType());

        // Act
        var change = (SubjectPropertyChange)method.Invoke(null,
            [_property, null, _changedTimestamp, _receivedTimestamp, oldValue, newValue])!;

        // Assert
        var getOldMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetOldValue))!
            .MakeGenericMethod(oldValue.GetType());
        var getNewMethod = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.GetNewValue))!
            .MakeGenericMethod(newValue.GetType());

        Assert.Equal(oldValue, getOldMethod.Invoke(change, null));
        Assert.Equal(newValue, getNewMethod.Invoke(change, null));
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(0, int.MaxValue)]
    public void Create_WithNullableInt_WithValue_StoresAndRetrievesCorrectly(int oldVal, int newVal)
    {
        // Arrange
        int? oldValue = oldVal;
        int? newValue = newVal;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableInt_WithNull_StoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = 42;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableInt_BothNull_StoresAndRetrievesCorrectly()
    {
        // Arrange
        int? oldValue = null;
        int? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int?>());
        Assert.Null(change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableDecimal_StoresAndRetrievesCorrectly()
    {
        // Arrange
        decimal? oldValue = 123.456m;
        decimal? newValue = null;

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Equal(oldValue, change.GetOldValue<decimal?>());
        Assert.Null(change.GetNewValue<decimal?>());
    }

    private struct SmallCustomStruct
    {
        public int Value1;
        public int Value2;
    }

    private struct LargeCustomStruct
    {
        public long Value1;
        public long Value2;
    }

    private struct OversizedCustomStruct
    {
        public long Value1;
        public long Value2;
        public long Value3; // 24 bytes total - exceeds 16 byte inline storage
    }

    [Fact]
    public void Create_WithSmallCustomStruct_StoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 1, Value2 = 2 };
        var newValue = new SmallCustomStruct { Value1 = 10, Value2 = 20 };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<SmallCustomStruct>();
        var retrievedNew = change.GetNewValue<SmallCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void Create_WithLargeCustomStruct_StoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new LargeCustomStruct { Value1 = 111111111111L, Value2 = 222222222222L };
        var newValue = new LargeCustomStruct { Value1 = 333333333333L, Value2 = 444444444444L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<LargeCustomStruct>();
        var retrievedNew = change.GetNewValue<LargeCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void Create_WithOversizedCustomStruct_StoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var newValue = new OversizedCustomStruct { Value1 = 10L, Value2 = 20L, Value3 = 30L };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        var retrievedOld = change.GetOldValue<OversizedCustomStruct>();
        var retrievedNew = change.GetNewValue<OversizedCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(oldValue.Value3, retrievedOld.Value3);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
        Assert.Equal(newValue.Value3, retrievedNew.Value3);
    }

    [Fact]
    public void GetOldValue_WithCustomStructAsObject_ReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new SmallCustomStruct { Value1 = 42, Value2 = 84 };
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, new SmallCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<SmallCustomStruct>(result);
        var unboxed = (SmallCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
    }

    [Fact]
    public void GetOldValue_WithOversizedStructAsObject_ReturnsBoxedStruct()
    {
        // Arrange
        var oldValue = new OversizedCustomStruct { Value1 = 1L, Value2 = 2L, Value3 = 3L };
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, new OversizedCustomStruct());

        // Act
        var result = change.GetOldValue<object>();

        // Assert
        Assert.IsType<OversizedCustomStruct>(result);
        var unboxed = (OversizedCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
        Assert.Equal(oldValue.Value3, unboxed.Value3);
    }

    private class CustomClass
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void Create_WithReferenceType_StoresAndRetrievesCorrectly()
    {
        // Arrange
        var oldValue = new CustomClass { Id = 1, Name = "Old" };
        var newValue = new CustomClass { Id = 2, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void Create_WithNullReferenceType_StoresAndRetrievesCorrectly()
    {
        // Arrange
        CustomClass? oldValue = null;
        var newValue = new CustomClass { Id = 1, Name = "New" };

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void Create_WithIntArray_StoresAndRetrievesCorrectly()
    {
        // Arrange
        int[] oldValue = [1, 2, 3];
        int[] newValue = [4, 5, 6, 7];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Same(oldValue, change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Fact]
    public void Create_WithNullArray_StoresAndRetrievesCorrectly()
    {
        // Arrange
        int[]? oldValue = null;
        int[] newValue = [1, 2, 3];

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        // Assert
        Assert.Null(change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Theory]
    [InlineData(42)]
    [InlineData("test")]
    public void GetOldValue_AsObject_ReturnsValue(object oldValue)
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, oldValue);

        // Assert
        var result = change.GetOldValue<object>();
        Assert.Equal(oldValue, result);
    }

    [Fact]
    public void TryGetOldValue_WithWrongType_ReturnsFalse()
    {
        // Arrange
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            42, 100);

        // Act
        var success = change.TryGetOldValue<string>(out var result);

        // Assert
        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetNewValue_WithCorrectType_ReturnsTrue()
    {
        // Arrange
        var newValue = 42.5;
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            0.0, newValue);

        // Act
        var success = change.TryGetNewValue<double>(out var result);

        // Assert
        Assert.True(success);
        Assert.Equal(newValue, result);
    }

    [Fact]
    public void Create_PreservesPropertyReference()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_property, change.Property);
    }

    [Fact]
    public void Create_PreservesSource()
    {
        // Arrange
        var source = new object();

        // Act
        var change = SubjectPropertyChange.Create(
            _property, source, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Same(source, change.Source);
    }

    [Fact]
    public void Create_PreservesTimestamps()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        // Assert
        Assert.Equal(_changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(_receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void Create_WithNullReceivedTimestamp_PreservesNull()
    {
        // Act
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, receivedTimestamp: null,
            "old", "new");

        // Assert
        Assert.Null(change.ReceivedTimestamp);
    }
}
