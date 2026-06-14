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

        public partial ImmutableArray<Tire> SpareTires { get; set; }

        public Garage()
        {
            Name = string.Empty;
            Cars = [];
            CarsByName = new Dictionary<string, Car>();
            SpareTires = [];
        }

        public override string ToString() => Name;
    }
}
