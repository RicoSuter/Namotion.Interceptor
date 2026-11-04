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
    .WithService<IPropertyValidator>(() => new NoSwearWordsValidator(), validator => validator is NoSwearWordsValidator);
```

Pass the `exists` predicate: without it, `WithService` skips the registration when any `IPropertyValidator` is already registered, for example the one added by `WithDataAnnotationValidation()`. Multiple validators can be registered this way and all will run. The errors from all validators are reported in a single `PropertyValidationException`, a `ValidationException` whose `ValidationResults` hold each result with its member names and whose message joins their messages, one per line. `Property` and `Value` identify what was rejected.

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
    .WithService<IPropertyValidator>(() => new MyCustomValidator(), validator => validator is MyCustomValidator);
```

Data Annotations and custom validators all run on each property write. If any validator returns errors, the write is rejected. Inside a transaction, the transaction's check mode decides when they run, see [Transactions](#transactions).

## Transactions

Inside a [transaction](tracking-transactions.md), writes are buffered until commit, where both check modes validate the final set. The transaction's `checkMode` decides whether writes are also validated when written:

| Mode | When a value is written |
|------|-------------------------|
| `Immediate` (default) | Validated against the transaction's values so far. An invalid write throws `PropertyValidationException` at the setter, so write order matters. |
| `Deferred` | Not validated. Code in the transaction's flow reads invalid intermediate values until a later write replaces them. |

In `Immediate` mode a write back to the landed value is validated too, so undoing a change can throw and leave it pending, while a write of the landed value without a pending change is dropped by the equality check and not validated. Writes to settable derived properties are not captured: they land immediately and are validated then, in both modes.

The samples use this model and validator:

```csharp
[InterceptorSubject]
public partial class Motor
{
    public partial int MaxAllowedSpeed { get; set; }

    public partial int MotorSpeed { get; set; }
}

public class MotorSpeedValidator : IPropertyValidator
{
    public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
    {
        if (context.Property.Name == nameof(Motor.MotorSpeed) &&
            context.Value is int speed &&
            context.Property.Subject is Motor motor &&
            speed > motor.MaxAllowedSpeed)
        {
            return [new ValidationResult($"MotorSpeed {speed} exceeds MaxAllowedSpeed {motor.MaxAllowedSpeed}.")];
        }

        return [];
    }
}
```

```csharp
using var transaction = await context.BeginTransactionAsync(
    TransactionFailureHandling.Rollback,
    checkMode: TransactionCheckMode.Deferred);

motor.MotorSpeed = 150;      // not validated yet
motor.MaxAllowedSpeed = 200; // the order does not matter

await transaction.CommitAsync(cancellationToken);
```

At commit:
- The [commit check](tracking-transactions.md#transaction-handlers) validates the final set, including the derived properties that depend on a changed property, with the validators of each subject's context before anything is written, reading the transaction's values, so write order does not matter.
- A commit check rejection makes `CommitAsync` throw `SubjectTransactionValidationException`, whose `FailedChanges` and `Errors` list the invalid changes, then the invalid derived properties, and their validation errors in the same order, and keeps the pending changes, so fix the values and [retry](tracking-transactions.md#retrying).
- With source transactions, the [pre-apply check](tracking-transactions.md#transaction-handlers) validates again right before the local apply and rejects terminally with a plain `SubjectTransactionException`, not a `SubjectTransactionValidationException`, whose `Errors` hold the validation errors, see the [Failure Table](tracking-transactions.md#failure-table) for its `FailedChanges` and what lands.
- Applied values, values a rollback restores and the derived properties they recalculate are not validated again, but other writes made during the apply, such as from change handlers or a hook that changes the value, are. For a failed apply, see the [Failure Table](tracking-transactions.md#failure-table).

### Writing Validators for Transactions

- **Arguments**: the type parameter is normally the property type but can be `object` (see `IPropertyValidator.Validate`), so test the value (`context.Value is int speed`), not `typeof(TProperty)`. `Origin` is the captured origin in the commit check and, in the pre-apply check, `Confirmed` with the accepting source for a value a source accepted; see [Writing a Transaction Handler](tracking-transactions.md#writing-a-transaction-handler) for skipping those or inbound values and for when the captured origin is `Local`.
- **Call count**: with source transactions, a value can be validated up to three times (on an `Immediate` write, in the commit check and in the pre-apply check), so validators must be free of side effects. Both checks also run the getters of the derived properties that depend on a changed property.
- **Errors**: each invalid property is reported as one `PropertyValidationException`, the exception that writes outside transactions and `Immediate` writes throw, see [Custom Validators](#custom-validators).
- **Reads**: properties without a pending change are read from the landed model, which writes outside transactions can change before the apply. Validate a rule on every property it reads, and do not mutate a value object after writing it. In `Deferred` mode with `FailOnConflict` (the default), write every value a rule depends on, changed or not, to check the rule against the final set, see [Check Mode](tracking-transactions.md#check-mode).
- **Object graph and timestamps**: during the checks, registry and lifecycle state such as parents and children reflect the landed graph, and `SubjectChangeContext.Current` does not carry the checked changes' timestamps. Only property reads return the transaction's values.
- **Derived properties**: both checks validate each derived property that depends on a changed property, computed over the transaction's values, so write order does not matter: with `Free => Capacity - Level` validated as non-negative, writing `Level` before `Capacity` commits when the final set is valid. An invalid derived property is reported in `FailedChanges` as a change of the derived property whose old and new value are its computed value. Recalculations on the committing thread while the commit applies or reverts its values see the values applied so far and are not validated. A recalculation that another thread is already running when the commit writes is run by that thread, which validates it, so an invalid value in between throws from that thread's write. So a derived value the checks did not compute over the final set is not validated: one whose getter throws over the final set, one that depends on a changed property only once a subject attaches during the apply, one that changes only through another write during the apply (for example from a change handler), a value written to a settable derived property during the apply, and a value computed after a write outside the transaction landed between the check and the apply.
- **New subjects**: only captured writes are validated, so the written subject must already use the context; one created without it and attached in the transaction is not validated.
- **Interceptor order**: `ValidationInterceptor` runs after the transaction interceptor and before derived property recalculation, change notifications and lifecycle handling. An interceptor that changes the written value or writes other properties must declare `[RunsBefore(typeof(SubjectTransactionInterceptor))]`, which ordering before `ValidationInterceptor` alone does not imply, otherwise the raw value is validated and captured and its own writes are not captured. Ordering one after `ValidationInterceptor` and before `SubjectTransactionInterceptor` is circular and throws `InvalidOperationException` at the first write. See [Edge Cases and Limitations](tracking-transactions.md#edge-cases-and-limitations) and [Service Ordering](interceptor.md#service-ordering).

## Dynamic Properties

Note that .NET's `Validator.TryValidateProperty` does not support dynamically added properties (via `Namotion.Interceptor.Dynamic` or registry). Data Annotation validation is automatically skipped for dynamic properties. Use custom validators if you need validation on dynamic properties.
