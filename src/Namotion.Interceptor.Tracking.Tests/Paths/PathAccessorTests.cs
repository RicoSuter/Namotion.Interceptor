using System.Collections.Immutable;
using Namotion.Interceptor.Tracking.Paths;
using Namotion.Interceptor.Tracking.Tests.Change;
using Namotion.Interceptor.Tracking.Tests.Models;

namespace Namotion.Interceptor.Tracking.Tests.Paths;

[Collection(PerPropertySubscriptionCollection.Name)]
public class PathAccessorTests
{
    [Fact]
    public void WhenImmutableArrayIsDefault_ThenIndexerReturnsNullNotThrow()
    {
        // Arrange
        var garage = new Garage();
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.SpareTires))!;
        var indexer = PathValueAccessors.GetImmutableArrayIndexer(typeof(Garage), propertyInfo, typeof(Tire));
        garage.SpareTires = default; // uninitialized ImmutableArray

        // Act
        var result = indexer(garage, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenImmutableArrayIndexInRange_ThenIndexerReturnsElement()
    {
        // Arrange
        var garage = new Garage();
        var tire = new Tire();
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.SpareTires))!;
        var indexer = PathValueAccessors.GetImmutableArrayIndexer(typeof(Garage), propertyInfo, typeof(Tire));
        garage.SpareTires = [tire];

        // Act
        var result = indexer(garage, 0);

        // Assert
        Assert.Same(tire, result);
    }

    [Fact]
    public void WhenImmutableArrayIndexOutOfRange_ThenIndexerReturnsNullNotThrow()
    {
        // Arrange
        var garage = new Garage();
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.SpareTires))!;
        var indexer = PathValueAccessors.GetImmutableArrayIndexer(typeof(Garage), propertyInfo, typeof(Tire));
        garage.SpareTires = [new Tire()];

        // Act
        var result = indexer(garage, 5);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenReferenceCollectionIndexOutOfRange_ThenReturnsNullNotThrow()
    {
        // Arrange
        var car = new Car(); // 4 tires

        // Act
        var result = PathValueAccessors.IndexReferenceCollection(car.Tires, 99);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenReferenceCollectionIsBoxedDefaultImmutableArray_ThenReturnsNullNotThrow()
    {
        // Arrange
        object boxedDefault = default(ImmutableArray<Tire>);

        // Act
        var result = PathValueAccessors.IndexReferenceCollection(boxedDefault, 0);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void WhenReferenceCollectionIndexInRange_ThenReturnsElement()
    {
        // Arrange
        var car = new Car(); // 4 tires
        var expected = car.Tires[2];

        // Act
        var result = PathValueAccessors.IndexReferenceCollection(car.Tires, 2);

        // Assert
        Assert.Same(expected, result);
    }

    [Fact]
    public void WhenLeafAccessorReadsValueType_ThenReturnsTypedValue()
    {
        // Arrange
        var tire = new Tire { Pressure = 2.5m };
        var propertyInfo = typeof(Tire).GetProperty(nameof(Tire.Pressure))!;
        var accessor = PathValueAccessors.GetLeafAccessor<decimal>(typeof(Tire), propertyInfo);

        // Act
        var result = accessor(tire);

        // Assert
        Assert.Equal(2.5m, result);
    }

    [Fact]
    public void WhenDictionaryKeyPresent_ThenLookupReturnsValue()
    {
        // Arrange
        var garage = new Garage();
        var car = new Car { Name = "Tesla" };
        garage.CarsByName = new Dictionary<string, Car> { ["Tesla"] = car };
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.CarsByName))!;
        var lookup = PathValueAccessors.GetDictionaryLookup(typeof(Garage), propertyInfo, BuildCarsByNameSegment());

        // Act
        var result = lookup(garage, "Tesla");

        // Assert
        Assert.Same(car, result);
    }

    [Fact]
    public void WhenDictionaryKeyMissing_ThenLookupReturnsNull()
    {
        // Arrange
        var garage = new Garage();
        garage.CarsByName = new Dictionary<string, Car> { ["Tesla"] = new Car { Name = "Tesla" } };
        var propertyInfo = typeof(Garage).GetProperty(nameof(Garage.CarsByName))!;
        var lookup = PathValueAccessors.GetDictionaryLookup(typeof(Garage), propertyInfo, BuildCarsByNameSegment());

        // Act
        var result = lookup(garage, "Missing");

        // Assert
        Assert.Null(result);
    }

    private static PathSegment BuildCarsByNameSegment()
        => new()
        {
            PropertyName = nameof(Garage.CarsByName),
            Kind = PathSegmentKind.DictionaryKey,
            PropertyStaticType = typeof(IReadOnlyDictionary<string, Car>),
            DictionaryInterfaceType = typeof(IReadOnlyDictionary<string, Car>),
            DictionaryKeyType = typeof(string),
            DictionaryValueType = typeof(Car),
        };
}
