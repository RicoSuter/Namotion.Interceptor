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
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Fact]
    public void Create_WithNullString_StoresAndRetrievesCorrectly()
    {
        string? oldValue = null;
        string? newValue = "NewName";

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<string>());
        Assert.Equal(newValue, change.GetNewValue<string>());
    }

    [Theory]
    [InlineData(42, 100)]
    [InlineData(int.MinValue, int.MaxValue)]
    [InlineData(0, -1)]
    public void Create_WithInt_StoresAndRetrievesCorrectly(int oldValue, int newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<int>());
        Assert.Equal(newValue, change.GetNewValue<int>());
    }

    [Theory]
    [InlineData(123456789012345L, 987654321098765L)]
    [InlineData(long.MinValue, long.MaxValue)]
    public void Create_WithLong_StoresAndRetrievesCorrectly(long oldValue, long newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<long>());
        Assert.Equal(newValue, change.GetNewValue<long>());
    }

    [Theory]
    [InlineData(3.14159265358979, 2.71828182845904)]
    [InlineData(double.MinValue, double.MaxValue)]
    public void Create_WithDouble_StoresAndRetrievesCorrectly(double oldValue, double newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<double>());
        Assert.Equal(newValue, change.GetNewValue<double>());
    }

    [Theory]
    [InlineData(3.14f, 2.71f)]
    [InlineData(float.MinValue, float.MaxValue)]
    public void Create_WithFloat_StoresAndRetrievesCorrectly(float oldValue, float newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<float>());
        Assert.Equal(newValue, change.GetNewValue<float>());
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Create_WithBool_StoresAndRetrievesCorrectly(bool oldValue, bool newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<bool>());
        Assert.Equal(newValue, change.GetNewValue<bool>());
    }

    [Theory]
    [InlineData((byte)0, (byte)255)]
    [InlineData((byte)128, (byte)64)]
    public void Create_WithByte_StoresAndRetrievesCorrectly(byte oldValue, byte newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<byte>());
        Assert.Equal(newValue, change.GetNewValue<byte>());
    }

    [Theory]
    [InlineData('A', 'Z')]
    [InlineData('0', '9')]
    public void Create_WithChar_StoresAndRetrievesCorrectly(char oldValue, char newValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<char>());
        Assert.Equal(newValue, change.GetNewValue<char>());
    }

    public static IEnumerable<object[]> LargerValueTypeTestData()
    {
        yield return new object[] { 123456789.123456789m, 987654321.987654321m };
        yield return new object[] { new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 12, 31, 23, 59, 59, DateTimeKind.Utc) };
        yield return new object[] { new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.FromHours(2)), new DateTimeOffset(2024, 12, 31, 23, 59, 59, TimeSpan.FromHours(-5)) };
        yield return new object[] { Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222") };
        yield return new object[] { TimeSpan.FromHours(1.5), TimeSpan.FromDays(7) };
    }

    [Theory]
    [MemberData(nameof(LargerValueTypeTestData))]
    public void Create_WithLargerValueTypes_StoresAndRetrievesCorrectly(object oldValue, object newValue)
    {
        var method = typeof(SubjectPropertyChange)
            .GetMethod(nameof(SubjectPropertyChange.Create))!
            .MakeGenericMethod(oldValue.GetType());

        var change = (SubjectPropertyChange)method.Invoke(null, new object?[]
        {
            _property, null, _changedTimestamp, _receivedTimestamp, oldValue, newValue
        })!;

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
        int? oldValue = oldVal;
        int? newValue = newVal;

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableInt_WithNull_StoresAndRetrievesCorrectly()
    {
        int? oldValue = null;
        int? newValue = 42;

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<int?>());
        Assert.Equal(newValue, change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableInt_BothNull_StoresAndRetrievesCorrectly()
    {
        int? oldValue = null;
        int? newValue = null;

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<int?>());
        Assert.Null(change.GetNewValue<int?>());
    }

    [Fact]
    public void Create_WithNullableDecimal_StoresAndRetrievesCorrectly()
    {
        decimal? oldValue = 123.456m;
        decimal? newValue = null;

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Equal(oldValue, change.GetOldValue<decimal?>());
        Assert.Null(change.GetNewValue<decimal?>());
    }

    [Fact]
    public void Create_WithNullableGuid_StoresAndRetrievesCorrectly()
    {
        Guid? oldValue = null;
        Guid? newValue = Guid.NewGuid();

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<Guid?>());
        Assert.Equal(newValue, change.GetNewValue<Guid?>());
    }

    public struct SmallCustomStruct
    {
        public int Value1;
        public int Value2;
    }

    public struct LargeCustomStruct
    {
        public long Value1;
        public long Value2;
    }

    [Fact]
    public void Create_WithSmallCustomStruct_StoresAndRetrievesCorrectly()
    {
        var oldValue = new SmallCustomStruct { Value1 = 1, Value2 = 2 };
        var newValue = new SmallCustomStruct { Value1 = 10, Value2 = 20 };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

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
        var oldValue = new LargeCustomStruct { Value1 = 111111111111L, Value2 = 222222222222L };
        var newValue = new LargeCustomStruct { Value1 = 333333333333L, Value2 = 444444444444L };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        var retrievedOld = change.GetOldValue<LargeCustomStruct>();
        var retrievedNew = change.GetNewValue<LargeCustomStruct>();
        Assert.Equal(oldValue.Value1, retrievedOld.Value1);
        Assert.Equal(oldValue.Value2, retrievedOld.Value2);
        Assert.Equal(newValue.Value1, retrievedNew.Value1);
        Assert.Equal(newValue.Value2, retrievedNew.Value2);
    }

    [Fact]
    public void GetOldValue_WithCustomStructAsObject_ReturnsBoxedStruct()
    {
        var oldValue = new SmallCustomStruct { Value1 = 42, Value2 = 84 };
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, new SmallCustomStruct());

        var result = change.GetOldValue<object>();

        Assert.IsType<SmallCustomStruct>(result);
        var unboxed = (SmallCustomStruct)result;
        Assert.Equal(oldValue.Value1, unboxed.Value1);
        Assert.Equal(oldValue.Value2, unboxed.Value2);
    }

    public class CustomClass
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void Create_WithReferenceType_StoresAndRetrievesCorrectly()
    {
        var oldValue = new CustomClass { Id = 1, Name = "Old" };
        var newValue = new CustomClass { Id = 2, Name = "New" };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Same(oldValue, change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void Create_WithNullReferenceType_StoresAndRetrievesCorrectly()
    {
        CustomClass? oldValue = null;
        var newValue = new CustomClass { Id = 1, Name = "New" };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<CustomClass>());
        Assert.Same(newValue, change.GetNewValue<CustomClass>());
    }

    [Fact]
    public void Create_WithIntArray_StoresAndRetrievesCorrectly()
    {
        var oldValue = new[] { 1, 2, 3 };
        var newValue = new[] { 4, 5, 6, 7 };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Same(oldValue, change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Fact]
    public void Create_WithNullArray_StoresAndRetrievesCorrectly()
    {
        int[]? oldValue = null;
        var newValue = new[] { 1, 2, 3 };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Null(change.GetOldValue<int[]>());
        Assert.Same(newValue, change.GetNewValue<int[]>());
    }

    [Fact]
    public void Create_WithList_StoresAndRetrievesCorrectly()
    {
        var oldValue = new List<int> { 1, 2, 3 };
        var newValue = new List<int> { 4, 5 };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Same(oldValue, change.GetOldValue<List<int>>());
        Assert.Same(newValue, change.GetNewValue<List<int>>());
    }

    [Fact]
    public void Create_WithDictionary_StoresAndRetrievesCorrectly()
    {
        var oldValue = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
        var newValue = new Dictionary<string, int> { ["x"] = 10 };

        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, newValue);

        Assert.Same(oldValue, change.GetOldValue<Dictionary<string, int>>());
        Assert.Same(newValue, change.GetNewValue<Dictionary<string, int>>());
    }

    [Theory]
    [InlineData(42)]
    [InlineData("test")]
    public void GetOldValue_AsObject_ReturnsValue(object oldValue)
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            oldValue, oldValue);

        var result = change.GetOldValue<object>();

        Assert.Equal(oldValue, result);
    }

    [Fact]
    public void TryGetOldValue_WithWrongType_ReturnsFalse()
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            42, 100);

        var success = change.TryGetOldValue<string>(out var result);

        Assert.False(success);
        Assert.Null(result);
    }

    [Fact]
    public void TryGetNewValue_WithCorrectType_ReturnsTrue()
    {
        var newValue = 42.5;
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            0.0, newValue);

        var success = change.TryGetNewValue<double>(out var result);

        Assert.True(success);
        Assert.Equal(newValue, result);
    }

    [Fact]
    public void Create_PreservesPropertyReference()
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        Assert.Equal(_property, change.Property);
    }

    [Fact]
    public void Create_PreservesSource()
    {
        var source = new object();

        var change = SubjectPropertyChange.Create(
            _property, source, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        Assert.Same(source, change.Source);
    }

    [Fact]
    public void Create_PreservesTimestamps()
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, _receivedTimestamp,
            "old", "new");

        Assert.Equal(_changedTimestamp, change.ChangedTimestamp);
        Assert.Equal(_receivedTimestamp, change.ReceivedTimestamp);
    }

    [Fact]
    public void Create_WithNullReceivedTimestamp_PreservesNull()
    {
        var change = SubjectPropertyChange.Create(
            _property, source: null, _changedTimestamp, receivedTimestamp: null,
            "old", "new");

        Assert.Null(change.ReceivedTimestamp);
    }
}
