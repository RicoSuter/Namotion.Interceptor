# Namotion.Proxy for .NET

Namotion.Proxy is a .NET library designed to simplify the creation of trackable object models by automatically generating property interceptors. All you need to do is annotate your model classes with a few simple attributes; they remain regular POCOs otherwise. The library uses source generation to handle the interception logic for you.

In addition to property tracking, Namotion.Proxy offers advanced features such as automatic change detection (including derived properties), reactive source mapping (e.g., for GraphQL subscriptions or MQTT publishing), and other powerful capabilities that integrate seamlessly into your workflow.

**The library is currently in development and the APIs might change.**

Feature map:

![features](./features.png)

## Change tracking sample

First you can define a proxied class:

```csharp
[GenerateProxy]
public partial class Person
{
    public partial string FirstName { get; set; }

    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}
```

With this implemented you can now create a proxy context and start tracking changes of these persons:

```csharp
var context = ProxyContext
    .CreateBuilder()
    .WithFullPropertyTracking()
    .Build();

context
    .GetPropertyChangedObservable()
    .Subscribe(change =>
    {
        Console.WriteLine(
            $"Property '{change.Property.Name}' changed " +
            $"from '{change.OldValue}' to '{change.NewValue}'.");
    });

var person = new Person(context)
{
    FirstName = "John",
// Property 'FirstName' changed from '' to 'John'.
// Property 'FullName' changed from ' ' to 'John '.

    LastName = "Doe"
// Property 'LastName' changed from '' to 'Doe'.
// Property 'FullName' changed from 'John ' to 'John Doe'.
};

person.FirstName = "Jane";
// Property 'FirstName' changed from 'John' to 'Jane'.
// Property 'FullName' changed from 'John Doe' to 'Jane Doe'.

person.LastName = "Smith";
// Property 'LastName' changed from 'Doe' to 'Smith'.
// Property 'FullName' changed from 'Jane Doe' to 'Jane Smith'.
```

## Proxy attach and detach tacking sample

Implement a class with properties which reference other proxied objects:

```csharp
[GenerateProxy]
public partial class Person
{
    public partial string Name { get; set; }

    public partial Person[] Children { get; set; }

    public Person()
    {
        Name = "n/a";
        Children = [];
    }

    public override string ToString()
    {
        return "Person: " + Name;
    }
 }
```

The context now automatically tracks the attachment and detachment of referenced proxies:

```csharp
var context = ProxyContext
    .CreateBuilder()
    .AddHandler(new LogPropertyChangesHandler())
    .WithFullPropertyTracking() // this will track property changes and proxy attaches/detaches
    .Build();

var child1 = new Person { Name = "Child1" };
var child2 = new Person { Name = "Child2" };
var child3 = new Person { Name = "Child3" };

var person = new Person(context)
// Attach proxy: Person: n/a

person.Children = 
[
    child1,
    child2
];
// Attach proxy: Person: Child1
// Attach proxy: Person: Child2

person.Children = 
[
    child1,
    child2,
    child3
];
// Attach proxy: Person: Child3

person.Children = [];
// Detach proxy: Person: Child1
// Detach proxy: Person: Child2
// Detach proxy: Person: Child3

public class LogPropertyChangesHandler : IProxyLifecycleHandler
{
    public void OnProxyAttached(ProxyLifecycleContext context)
    {
        Console.WriteLine($"Attach proxy: {context.Proxy}");
    }

    public void OnProxyDetached(ProxyLifecycleContext context)
    {
        Console.WriteLine($"Detach proxy: {context.Proxy}");
    }
}
```

## More samples

For more samples, check out the "Samples" directory in the Visual Studio solution.
