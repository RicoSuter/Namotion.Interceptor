using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models;

/// <summary>
/// Model with structural properties typed as struct collections. Their default instance holds a
/// null inner array, so such a property starts out in a state that throws when read, and
/// <c>= default</c> is legal C# that puts it back there.
/// </summary>
[InterceptorSubject]
public partial class StructCollectionHolder
{
    /// <summary>Boxes to an <see cref="ICollection"/>, the collection dispatch arm.</summary>
    public partial ImmutableArray<Car> ImmutableCars { get; set; }

    /// <summary>Boxes to a bare <see cref="IEnumerable"/>, the read-only collection dispatch arm.</summary>
    public partial ArraySegment<Car> SegmentCars { get; set; }

    /// <summary>Interface-declared, so the declared type cannot tell a default from a live value.</summary>
    public partial IEnumerable<Car>? InterfaceCars { get; set; }
}

/// <summary>
/// Read-only struct dictionary that does not defend against its own default instance, so every
/// read of <c>default(RawCarMap)</c> throws.
/// </summary>
public readonly struct RawCarMap(Dictionary<string, Car> cars) : IReadOnlyDictionary<string, Car>
{
    private readonly Dictionary<string, Car> _cars = cars;

    public Car this[string key] => _cars[key];

    public IEnumerable<string> Keys => _cars.Keys;

    public IEnumerable<Car> Values => _cars.Values;

    public int Count => _cars.Count;

    public bool ContainsKey(string key) => _cars.ContainsKey(key);

    public bool TryGetValue(string key, out Car value) => _cars.TryGetValue(key, out value!);

    public IEnumerator<KeyValuePair<string, Car>> GetEnumerator() => _cars.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Struct collection that declares a parameterless constructor, so <c>Activator.CreateInstance</c> would not produce its default.</summary>
public struct ConstructedCarBag : IEnumerable<Car>
{
    public int Marker;

    public ConstructedCarBag()
    {
        Marker = 42;
    }

    public readonly IEnumerator<Car> GetEnumerator() => throw new NotSupportedException();

    readonly IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
