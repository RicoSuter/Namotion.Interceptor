using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change.Attributes;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.SampleConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var collection = InterceptorSubjectContext
                .Create()
                .WithService(() => new LogPropertyChangesHandler())
                .WithFullPropertyTracking();

            collection
                .GetPropertyChangedObservable()
                .Subscribe((change) => 
                    Console.WriteLine($"Property {change.Property.Name} changed from {change.OldValue} to {change.NewValue}."));

            var child1 = new Person { FirstName = "Child1" };
            var child2 = new Person { FirstName = "Child2" };
            var child3 = new Person { FirstName = "Child3" };

            var person = new Person(collection)
            {
                FirstName = "Rico",
                LastName = "Suter",
                Mother = new Person
                {
                    FirstName = "Susi"
                },
                Children = 
                [
                    child1,
                    child2
                ]
            };

            person.Children = 
            [
                child1,
                child2,
                child3
            ];

            person.Children = [];

            Console.WriteLine($"Person's first name is: {person.FirstName}");
        }
    }

    [InterceptorSubject]
    public partial class Person
    {
        public partial string FirstName { get; set; }

        public partial string? LastName { get; set; }

        [Derived]
        public string FullName => $"{FirstName} {LastName}";

        public partial Person? Father { get; set; }

        public partial Person? Mother { get; set; }

        public partial Person[] Children { get; set; }

        public Person()
        {
            Children = [];
        }

        public override string ToString()
        {
            return "Person: " + FullName;
        }
    }

    public class LogPropertyChangesHandler : ILifecycleHandler
    {
        public void Attach(LifecycleContext context)
        {
            Console.WriteLine($"Attach proxy: {context.Subject}");
        }

        public void Detach(LifecycleContext context)
        {
            Console.WriteLine($"Detach proxy: {context.Subject}");
        }
    }
}
