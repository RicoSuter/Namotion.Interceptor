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

Data Annotations and custom validators all run on each property write. If any validator returns errors, the write is rejected. Inside a transaction, the transaction's validation mode decides when they run, see [Transactions](#transactions).

## Transactions

Inside a [transaction](tracking-transactions.md), writes are buffered until commit. Both validation modes validate the final set at commit; the transaction's `validationMode` decides whether writes are also validated when written:

| Mode | When a value is written |
|------|-------------------------|
| `Immediate` (default) | Validated against the transaction's values so far. An invalid write throws `ValidationException` at the setter, so write order matters there. This includes a write back to the landed value: if the landed value is invalid against the transaction's other values, undoing a change throws and the change stays pending. |
| `Deferred` | Not validated. Invalid intermediate values are accepted, and code in the transaction's flow reads them until a later write replaces them. |

```csharp
using var transaction = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    validationMode: TransactionValidationMode.Deferred);

motor.MotorSpeed = 150;      // not validated yet
motor.MaxAllowedSpeed = 200; // the order does not matter

await transaction.CommitAsync(cancellationToken);
```

At commit:
- The [commit check](tracking-transactions.md#transaction-handlers) validates every changed property with the validators of its subject's context, before anything is written. Reads return the transaction's values, so write order does not matter (except for derived properties, see below).
- An invalid value makes `CommitAsync` throw `SubjectTransactionValidationException`. Its `FailedChanges` and `Errors` list the invalid changes and their validation errors in the same order. The pending changes stay intact: fix the values and commit again, see [Retrying](tracking-transactions.md#retrying).
- With source transactions, the [pre-apply check](tracking-transactions.md#transaction-handlers) validates the changes again right before the local apply. A failure there is terminal and lands no value of the set. It throws a `SubjectTransactionException`, not a `SubjectTransactionValidationException`, whose `FailedChanges` lists every change and whose `Errors` hold the validation errors.
- The values the commit applies and the values a rollback restores are not validated again. Writes made during the apply, for example from change handlers or a hook that changes the value, and recalculated derived properties are validated as usual, after any source write.
- If applying one value fails, for example because a hook throws, no value of the set lands in either failure handling mode, except the value whose apply failed after it was written ([#674](https://github.com/RicoSuter/Namotion.Interceptor/issues/674)), see [Commit Failures](tracking-transactions.md#commit-failures).

### Writing Validators for Transactions

- **Arguments**: the type parameter is unspecified, so test the value (`context.Value is int speed`), not `typeof(TProperty)`. `Origin` is the captured origin of the change. As a performance detail, both checks currently pass the property type for `int`, `long`, `double`, `float`, `decimal`, `bool`, `DateTime`, `DateTimeOffset`, `TimeSpan` and `Guid`, so the check does not box these values, and `object` with the boxed value for every other type, including their nullable forms.
- **Call count**: with source transactions, a value can be validated up to three times: when written in `Immediate` mode, in the commit check, and in the pre-apply check. Validators must be free of side effects.
- **Errors**: each invalid property is reported as one `ValidationException` whose message joins the messages of all its errors. The individual `ValidationResult`s, including their member names, are not kept.
- **Reads**: properties without a pending change are read from the landed model, and writes outside transactions can still change them before the apply. Validate a rule on every property it reads, and do not mutate a value object after writing it to a property.
- **Object graph and timestamps**: during the checks, registry and lifecycle state, such as parents and children, reflect the landed graph, and `SubjectChangeContext.Current` does not carry the timestamps of the checked changes. Only property reads return the transaction's values.
- **Derived properties**: a derived property is validated only when the commit applies a value it depends on and recalculates it, after any source write, so it cannot reject the commit before the sources are written. The values are applied one at a time, so the recalculation sees intermediate values and write order matters: with `Free => Capacity - Level` validated as non-negative, writing `Level` before `Capacity` can fail the apply although the final set is valid. The value whose apply triggered the failed recalculation (here `Level`) stays in the local model, because the failed apply itself is not reverted ([#674](https://github.com/RicoSuter/Namotion.Interceptor/issues/674)).
- **New subjects**: only writes the transaction captures are validated, so the written subject must already use the context. A subject created without the context and attached in the transaction is not validated.

### Interceptor Order

`ValidationInterceptor` runs after the transaction interceptor and before derived property recalculation, change notifications and lifecycle handling. A custom write interceptor without ordering attributes is placed by registration order and can end up on either side of validation and of the transaction interceptor. An interceptor that changes the written value or writes other properties must declare `[RunsBefore(typeof(SubjectTransactionInterceptor))]`: after the transaction interceptor it runs only when the commit applies the value, so inside a transaction the raw value is validated and captured, and its own writes are not captured. `[RunsBefore(typeof(ValidationInterceptor))]` alone does not place it before the transaction interceptor.

## Dynamic Properties

Note that .NET's `Validator.TryValidateProperty` does not support dynamically added properties (via `Namotion.Interceptor.Dynamic` or registry). Data Annotation validation is automatically skipped for dynamic properties. Use custom validators if you need validation on dynamic properties.
