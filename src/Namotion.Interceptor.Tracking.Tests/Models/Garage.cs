using System.Collections;
using System.Collections.Immutable;
using Namotion.Interceptor.Attributes;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class Garage
    {
        public partial string Name { get; set; }

        public partial IReadOnlyList<Car> Cars { get; set; }

        public partial IReadOnlyDictionary<string, Car> CarsByName { get; set; }

        public partial IReadOnlyDictionary<object, Car> CarsByOpaqueKey { get; set; }

        public partial Car? PrimaryCar { get; set; }

        public partial Car[] CarArray { get; set; }

        public partial List<Car> MutableCars { get; set; }

        public partial ICollection CollectionItems { get; set; }

        public partial IEnumerable<object?> EnumerableItems { get; set; }

        public partial IDictionary DictionaryItems { get; set; }

        public partial ImmutableArray<Tire> SpareTires { get; set; }

        public Garage()
        {
            Name = string.Empty;
            Cars = [];
            CarsByName = new Dictionary<string, Car>();
            CarsByOpaqueKey = new Dictionary<object, Car>();
            PrimaryCar = null;
            CarArray = [];
            MutableCars = [];
            CollectionItems = Array.Empty<object>();
            EnumerableItems = [];
            DictionaryItems = new Hashtable();
            SpareTires = [];
        }

        public override string ToString() => Name;
    }
}
