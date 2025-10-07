﻿using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Lifecycle;

namespace Namotion.Interceptor.SampleConsole
{
    internal class Program
    {
        static void Main(string[] args)
        {
            var context = InterceptorSubjectContext
                .Create()
                .WithService(() => new LogPropertyChangesHandler())
                .WithFullPropertyTracking();

            context
                .GetPropertyChangedObservable()
                .Subscribe((change) => 
                    Console.WriteLine($"Property {change.Property.Name} changed from {change.GetOldValue<object?>()} to {change.GetNewValue<object?>()}."));

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
        public void AttachSubject(SubjectLifecycleChange change)
        {
            Console.WriteLine($"Attach: {change.Subject}");
        }

        public void DetachSubject(SubjectLifecycleChange change)
        {
            Console.WriteLine($"Detach: {change.Subject}");
        }
    }
}
