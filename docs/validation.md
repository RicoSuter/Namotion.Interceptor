# Validation

The `Namotion.Interceptor.Validation` package provides automatic property validation using Data Annotations or custom validators. Validation runs when properties are written, throwing a `ValidationException` if the new value is invalid.

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

The `PropertyValidationContext` carries the property, the new value, and the attempted `Origin` of the write (`Local` for user writes, `FromSource` for inbound source applies). Provenance-aware validators can treat source values as authoritative while strictly validating local input. Because the context is passed by `in`, implementations must return a collection instead of using `yield`.

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

Data Annotations and custom validators all run on each property write (in transactions, see [Transactions](#transactions)). If any validator returns errors, the write is rejected.

## Transactions

Inside a [transaction](tracking-transactions.md), writes are buffered until commit. The transaction's `validationMode` decides when validators run:

| Mode | When a value is written | At commit |
|------|-------------------------|-----------|
| `Immediate` (default) | Validated against the transaction's values so far | The final set is validated before any source write or local apply |
| `Deferred` | Not validated | The final set is validated before any source write or local apply |

```csharp
using var transaction = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    validationMode: TransactionValidationMode.Deferred);

motor.MotorSpeed = 150;      // not validated yet
motor.MaxAllowedSpeed = 200; // the order does not matter

await transaction.CommitAsync(cancellationToken);
```

The commit check validates every changed property with the validators of its subject's own context while reads return the transaction's values, so the order of the writes does not matter. If a value is invalid, `CommitAsync` throws `SubjectTransactionValidationException` before anything is written. Its `FailedChanges` and `Errors` list the invalid changes and their validation errors in the same order. The pending changes stay intact, so the values can be fixed and the commit retried.

With source transactions, the final set is checked again right before the local apply, after the sources were written, because an inbound update can change a value a validator reads. A failure there is handled like a failed local apply: it reverts the affected source writes, arrives as a `SubjectTransactionException`, and ends the transaction.

In `Immediate` mode the setter still throws `ValidationException` for a write that is invalid against the values written so far, so write order matters at the setter. `Deferred` accepts invalid intermediate values, and code running in the transaction's flow reads them until a later write replaces them or the commit checks the final value.

The values a commit applies are not validated again, and neither are the values a rollback restores. Writes made while a commit applies its values, for example from change handlers or a hook that changes the value, and recalculated derived properties are validated as usual, after any source write. With `BestEffort`, if applying one committed value fails, for example a hook throws, the other values still land; they are not checked again as a set.

During the commit checks, validators are called with `object` as the type parameter and the boxed value, and `Origin` is the captured origin of the change. Test the value (`context.Value is int speed`) rather than `typeof(TProperty)`.

The commit check reads properties outside the transaction from the model. Writes outside transactions are not blocked by a commit, so such a property can still change before the apply. Validate a rule on every property it reads, and do not mutate a value object after writing it to a property.

### Interceptor Order

`ValidationInterceptor` runs after the transaction interceptor and before derived property recalculation, change notifications and lifecycle handling. A custom write interceptor without ordering attributes is placed by registration order and can end up on either side of validation and of the transaction interceptor. Declare `[RunsBefore]` or `[RunsAfter]` when its position matters.

## Dynamic Properties

Note that .NET's `Validator.TryValidateProperty` does not support dynamically added properties (via `Namotion.Interceptor.Dynamic` or registry). Data Annotation validation is automatically skipped for dynamic properties. Use custom validators if you need validation on dynamic properties.
