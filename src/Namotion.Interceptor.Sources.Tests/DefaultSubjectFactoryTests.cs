using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor.Registry.Abstractions;
using Namotion.Interceptor.Sources.Tests.Models;

namespace Namotion.Interceptor.Sources.Tests;

public class DefaultSubjectFactoryTests
{
    public class MyClass : IInterceptorSubject
    {
        public object Injected { get; }
        
        public MyClass(object injected)
        {
            Injected = injected;
        }

        public IInterceptorSubjectContext Context { get; } = null!;
        public ConcurrentDictionary<string, object?> Data { get; } = null!;
        public IReadOnlyDictionary<string, SubjectPropertyMetadata> Properties { get; } = null!;
    }
    
    [Fact]
    public void WhenCreatingSubject_ThenServiceProviderIsUsed()
    {
        // Arrange
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<object>(42);

        var context = new InterceptorSubjectContext();
        context.AddService(serviceCollection.BuildServiceProvider());

        var person = new Person(context);
        var subject = new RegisteredSubject(person, [
            RegisteredSubjectProperty.Create(
                new PropertyReference(person, nameof(Person.Mother)),
                typeof(MyClass),
                [])
        ]);

        var property = subject.PropertiesAndAttributes.First();

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var myClass = subjectFactory.CreateSubject(property, null) as MyClass;
        
        // Assert
        Assert.NotNull(myClass);
        Assert.Equal(42, myClass.Injected);
    }
    
    [Fact]
    public void WhenCreatingSubjectCollection_ThenItWorks()
    {
        // Arrange
        var context = new InterceptorSubjectContext();
        var person = new Person(context);
        var subject = new RegisteredSubject(person, [
            RegisteredSubjectProperty.Create(
                new PropertyReference(person, nameof(Person.Mother)),
                typeof(IList<MyClass>),
                [])
        ]);

        var property = subject.PropertiesAndAttributes.First();

        // Act
        var subjectFactory = new DefaultSubjectFactory();
        var myClassCollection = subjectFactory.CreateSubjectCollection(property, new MyClass(1), new MyClass(2)) as IList<MyClass>;
        
        // Assert
        Assert.NotNull(myClassCollection);
        Assert.Equal(2, myClassCollection.Count());
    }
}