# Validation

The `Namotion.Interceptor.Validation` package provides automatic property validation using Data Annotations or custom validators. Validation runs when properties are written **locally**, throwing a `ValidationException` if the new value is invalid.

## Validation is scoped to local writes

Validators run only for locally originated writes. Values a source sent (`FromSource`) or confirmed through a transaction commit (`Confirmed`) are applied without validation.

The external system is the source of truth and the subject is a replica of it, so a value that has already been sent or accepted by a source is outside the model's control. Rejecting it cannot cleanly undo anything: an inbound value would simply leave the model diverged from its source, and a confirmed commit value would make the transaction revert a write the source already accepted, which other subscribers of that source observe as a value flap.

Local writes keep their veto, which is where protective rules belong. Rejecting user input, forbidding application writes to a source-driven property, and permission checks are all local-write rejections and are unaffected. So is a local transaction's commit replay, which is still validated because a rejection there is cleanly recoverable: nothing has been published outward, so the change is simply reported as failed and the model keeps its old value.

One consequence is worth spelling out: an inbound source write skips validation, but a derived property that recalculates because of it is a separate, locally computed write, so that recalculation is still validated. A validator can therefore still reject during an inbound apply by way of a derived cascade. The connector logs that failure and continues with the next change.

Note that this describes the write interceptor. Code that resolves `IPropertyValidator` and invokes it directly, such as the ASP.NET Core update endpoint, is validating local input before writing and is unaffected.

## Setup

For standard Data Annotation validation (most common):

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation();
```

If you only need custom validators without Data Annotations:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyValidation();
```

## Data Annotation Validation

Use standard .NET Data Annotation attributes on your properties:

```csharp
[InterceptorSubject]
public partial class Person
{
    [Required]
    [MaxLength(50)]
    public partial string FirstName { get; set; }

    [Range(0, 150)]
    public partial int Age { get; set; }

    [EmailAddress]
    public partial string? Email { get; set; }
}
```

Validation runs automatically on property writes:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation();

var person = new Person(context);

person.FirstName = "John";  // OK
person.Age = 25;            // OK

person.FirstName = "This name is way too long and exceeds the maximum length";
// Throws ValidationException

person.Age = -5;
// Throws ValidationException: The field Age must be between 0 and 150.
```

The original value is preserved when validation fails:

```csharp
person.FirstName = "Rico";  // OK

try
{
    person.FirstName = "This is too long";
}
catch (ValidationException)
{
    // Validation failed
}

Console.WriteLine(person.FirstName);  // Still "Rico"
```

## Custom Validators

For validation logic beyond Data Annotations, implement `IPropertyValidator`:

```csharp
public class NoSwearWordsValidator : IPropertyValidator
{
    private static readonly string[] BadWords = ["bad", "words"];

    public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
    {
        if (context.Value is not string text)
        {
            return [];
        }

        List<ValidationResult>? results = null;
        foreach (var word in BadWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                results ??= [];
                results.Add(new ValidationResult(
                    $"Property '{context.Property.Name}' contains prohibited word: {word}"));
            }
        }

        return results ?? [];
    }
}
```

The `PropertyValidationContext` carries the property, the new value, and the `Origin` of the write. When the validator is invoked by the write interceptor the origin is always `Local`, because [validation is scoped to local writes](#validation-is-scoped-to-local-writes); the property remains on the context for callers that construct it and invoke validators directly. Because the context is passed by `in`, implementations must return a collection instead of using `yield`.

Register your custom validator:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyValidation()
    .WithService<IPropertyValidator>(() => new NoSwearWordsValidator());
```

Multiple validators can be registered and all will run. Errors from all validators are combined into a single `ValidationException`.

### Use Cases for Custom Validators

- **Cross-property validation**: Access other properties via `context.Property.Subject`
- **External validation**: Check against databases, APIs, or configuration
- **Complex business rules**: Validation logic that doesn't fit in attributes
- **Conditional validation**: Rules that depend on subject state

## Combining Data Annotations and Custom Validators

Both can be used together:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation()  // Includes WithPropertyValidation()
    .WithService<IPropertyValidator>(() => new MyCustomValidator());
```

Data Annotations and custom validators all run on each property write. If any validator returns errors, the write is rejected.

## Dynamic Properties

Note that .NET's `Validator.TryValidateProperty` does not support dynamically added properties (via `Namotion.Interceptor.Dynamic` or registry). Data Annotation validation is automatically skipped for dynamic properties. Use custom validators if you need validation on dynamic properties.
