using Namotion.Proxy;
using Namotion.Proxy.Abstractions;

namespace ConsoleApp1
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var context = ProxyContext
                .CreateBuilder()
                .AddHandler(new LogPropertyChangesHandler())
                .WithFullPropertyTracking()
                .WithPropertyChangedCallback((ctx) => 
                    Console.WriteLine($"Property {ctx.PropertyName} changed from {ctx.OldValue} to {ctx.NewValue}."))
                .Build();

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
                Children = [
                    child1,
                    child2
                ]
            };

            person.Children = [
                child1,
                child2,
                child3
            ];

            person.Children = Array.Empty<Person>();

            Console.WriteLine(person.FirstName);
        }
    }

    [GenerateProxy]
    public abstract class PersonBase
    {
        public virtual required string FirstName { get; set; }

        public virtual string? LastName { get; set; }

        public virtual string FullName => $"{FirstName} {LastName}";

        public virtual Person? Father { get; set; }

        public virtual Person? Mother { get; set; }

        public virtual Person[] Children { get; set; } = Array.Empty<Person>();

        public override string ToString()
        {
            return "Person: " + FullName;
        }
    }

    public class LogPropertyChangesHandler : IProxyPropertyRegistryHandler
    {
        public void AttachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
        {
            Console.WriteLine($"AttachProxy: {proxy}");
        }

        public void DetachProxy(ProxyPropertyRegistryHandlerContext context, IProxy proxy)
        {
            Console.WriteLine($"DetachProxy: {proxy}");
        }
    }
}
