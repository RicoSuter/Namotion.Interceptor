using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.Tracking.Tests.Models
{
    [InterceptorSubject]
    public partial class Car : ILifecycleHandler
    {
        public partial string Name { get; set; }

        public partial Tire[] Tires { get; set; }
        
        [Derived]
        public decimal AveragePressure => Tires.Average(t => t.Pressure);

        public Car()
        {
            Name = string.Empty;
            Tires = Enumerable
                .Range(1, 4)
                .Select(_ => new Tire())
                .ToArray();
        }

        public List<SubjectLifecycleChange> Attachements { get; } = new();
        
        public List<SubjectLifecycleChange> Detachements { get; } = new();

        public void Attach(SubjectLifecycleChange change)
        {
            Attachements.Add(change);
        }
        
        public void Detach(SubjectLifecycleChange change)
        {
            Detachements.Add(change);
        }
    }
}