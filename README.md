# Namotion.Proxy for .NET

Namotion.Proxy is a .NET library designed to simplify the creation of trackable object models by automatically generating property interceptors. All you need to do is annotate your model classes with a few simple attributes; they remain regular POCOs otherwise. The library uses source generation to handle the interception logic for you.

In addition to property tracking, Namotion.Proxy offers advanced features such as automatic change detection (including derived properties), reactive source mapping (e.g., for GraphQL subscriptions or MQTT publishing), and other powerful capabilities that integrate seamlessly into your workflow.

**The library is currently in development and the APIs might change.**

Feature map:

![features](./features.png)

## Sample

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
// Create a proxy context with property tracking
var context = ProxyContext
    .CreateBuilder()
    .WithFullPropertyTracking()
    .Build();

// Subscribe to property change notifications
context
    .GetPropertyChangedObservable()
    .Subscribe(change =>
    {
        Console.WriteLine(
            $"Property '{change.Property.Name}' changed " +
            $"from '{change.OldValue}' to '{change.NewValue}'.");
    });

// Create a person with proxy tracking
var person = new Person(context)
{
    FirstName = "John",
    LastName = "Doe"
};

// Modify properties to trigger change notifications
person.FirstName = "Jane";
person.LastName = "Smith";
```

The output looks as follows:

```ps
Property 'FirstName' changed from '' to 'John'.
Property 'FullName' changed from ' ' to 'John '.
Property 'LastName' changed from '' to 'Doe'.
Property 'FullName' changed from 'John ' to 'John Doe'.
Property 'FirstName' changed from 'John' to 'Jane'.
Property 'FullName' changed from 'John Doe' to 'Jane Doe'.
Property 'LastName' changed from 'Doe' to 'Smith'.
Property 'FullName' changed from 'Jane Doe' to 'Jane Smith'.
```
