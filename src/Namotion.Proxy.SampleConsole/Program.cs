using Namotion.Interception.Lifecycle.Abstractions;
using Namotion.Interception.Lifecycle.Attributes;
using Namotion.Proxy.Abstractions;

namespace Namotion.Proxy.SampleConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var context = InterceptorContext
                .CreateBuilder()
                .TryAddSingleton<ILifecycleHandler, LogPropertyChangesHandler>((_, _) => new LogPropertyChangesHandler())
                .WithFullPropertyTracking()
                .Build();

            context
                .GetPropertyChangedObservable()
                .Subscribe((change) => 
                    Console.WriteLine($"Property {change.Property.Name} changed from {change.OldValue} to {change.NewValue}."));

            var child1 = new Person { FirstName = "Child1" };
            var child2 = new Person { FirstName = "Child2" };
            var child3 = new Person { FirstName = "Child3" };

            var person = new Person(context)
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

    [GenerateProxy]
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
        public void AddChild(LifecycleContext context)
        {
            Console.WriteLine($"Attach proxy: {context.Subject}");
        }

        public void RemoveChild(LifecycleContext context)
        {
            Console.WriteLine($"Detach proxy: {context.Subject}");
        }
    }
}
