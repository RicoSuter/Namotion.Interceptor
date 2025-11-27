# Transactions

The `Namotion.Interceptor.Tracking` package provides transaction support for batching property changes and committing them atomically. This is particularly useful when integrating with external data sources (OPC UA, MQTT, databases) where you want to write multiple changes as a single operation, or when you need to ensure consistency across multiple property updates.

## Overview

Transactions provide:
- **Atomic commits**: All changes are applied together or not at all
- **Read-your-writes consistency**: Reading a property inside a transaction returns the pending value
- **Notification suppression**: Change notifications are suppressed during the transaction and fired after commit
- **External source integration**: Changes can be written to external sources before being applied to the in-process model
- **Rollback on dispose**: Uncommitted changes are discarded when the transaction is disposed

## Setup

Enable transaction support in your interceptor context:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking(); // Includes WithTransactions()
```

Or enable transactions individually:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithTransactions()
    .WithPropertyChangeObservable(); // Transactions should be registered BEFORE notifications
```

> **Important**: `WithTransactions()` should be registered before `WithPropertyChangeObservable()` or `WithPropertyChangeQueue()` to ensure change notifications are suppressed during the transaction capture phase.

## Basic Usage

### Starting a Transaction

```csharp
var person = new Person(context);

using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // Changes are captured but not yet applied to the model
    // No change notifications are fired yet

    await transaction.CommitAsync();

    // All changes are now applied and notifications are fired
}
```

### Read-Your-Writes Consistency

Inside a transaction, reading a property returns the pending value:

```csharp
using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";

    // Reading returns the pending value, not the committed value
    Console.WriteLine(person.FirstName); // Output: John

    await transaction.CommitAsync();
}
```

### Rollback (Implicit)

If a transaction is disposed without committing, changes are discarded:

```csharp
using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";

    // No commit - changes are discarded when using block exits
}

Console.WriteLine(person.FirstName); // Output: (original value, not "John")
```

### Inspecting Pending Changes

You can inspect which changes are pending before committing:

```csharp
using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";
    person.LastName = "Doe";

    var pendingChanges = transaction.GetPendingChanges();
    foreach (var change in pendingChanges)
    {
        Console.WriteLine($"{change.Property.Name}: {change.GetOldValue<object>()} -> {change.GetNewValue<object>()}");
    }

    await transaction.CommitAsync();
}
```

## Last-Write-Wins Semantics

If the same property is written multiple times within a transaction, only the final value is committed:

```csharp
using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";
    person.FirstName = "Jane";
    person.FirstName = "Bob";

    // Only one pending change for FirstName
    // OldValue = original value, NewValue = "Bob"

    await transaction.CommitAsync();
}
```

## Exception Handling

### Already Committed

Calling `CommitAsync()` twice throws an exception:

```csharp
using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";
    await transaction.CommitAsync();

    await transaction.CommitAsync(); // Throws InvalidOperationException
}
```

### Disposed Transaction

Using a disposed transaction throws an exception:

```csharp
var transaction = SubjectTransaction.BeginTransaction();
transaction.Dispose();

await transaction.CommitAsync(); // Throws ObjectDisposedException
```

### Nested Transactions

Nested transactions are not supported:

```csharp
using (var transaction1 = SubjectTransaction.BeginTransaction())
{
    using (var transaction2 = SubjectTransaction.BeginTransaction()) // Throws InvalidOperationException
    {
    }
}
```

## External Source Integration

When integrating with external data sources (OPC UA, MQTT, databases), use `WithSourceTransactions()` from the `Namotion.Interceptor.Sources` package:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithFullPropertyTracking()
    .WithSourceTransactions(); // Enables external source writes
```

With this configuration, `CommitAsync()` will:

1. **Write to external sources first**: Changes are grouped by their associated source and written in batches
2. **Handle partial failures**: If some sources fail, successful changes are still applied
3. **Apply to in-process model**: After external writes complete, changes are applied to the model
4. **Report failures**: An `AggregateException` is thrown containing all source write failures

```csharp
try
{
    using (var transaction = SubjectTransaction.BeginTransaction())
    {
        // These properties are bound to an OPC UA source
        device.Temperature = 25.5;
        device.Pressure = 101.3;

        await transaction.CommitAsync();
    }
}
catch (AggregateException ex)
{
    foreach (var failure in ex.InnerExceptions.OfType<SourceWriteException>())
    {
        Console.WriteLine($"Failed to write to {failure.Source}: {failure.InnerException.Message}");
    }
}
```

### Source Association

Properties must be associated with a source for external writes:

```csharp
// In your source implementation
propertyReference.SetSource(this); // Associate property with this source

// Later, when the transaction commits, WriteChangesInBatchesAsync is called on the source
```

Properties without an associated source are applied directly to the in-process model without external writes.

## Derived Properties

Derived properties (marked with `[Derived]`) are handled specially during transactions:

- **During capture**: Derived property recalculation is skipped
- **During read**: Derived properties return their calculated value based on pending changes
- **After commit**: Derived properties are recalculated with the committed values

```csharp
[InterceptorSubject]
public partial class Person
{
    public partial string FirstName { get; set; }
    public partial string LastName { get; set; }

    [Derived]
    public string FullName => $"{FirstName} {LastName}";
}

using (var transaction = SubjectTransaction.BeginTransaction())
{
    person.FirstName = "John";
    person.LastName = "Doe";

    // FullName is calculated from pending values
    Console.WriteLine(person.FullName); // Output: John Doe

    await transaction.CommitAsync();
    // FullName change notification is fired after commit
}
```

## Thread Safety

- `SubjectTransaction.BeginTransaction()` uses `AsyncLocal<T>` to store the current transaction, making it safe for async/await patterns
- Each async execution context has its own transaction scope
- The transaction is automatically cleared when `Dispose()` is called
- On `CommitAsync()`, write callbacks are resolved from each subject's context, supporting multi-context scenarios

## Best Practices

1. **Always use `using` blocks**: This ensures transactions are properly disposed even if exceptions occur

2. **Keep transactions short**: Long-running transactions hold pending changes in memory

3. **Register transactions before notifications**: Ensure `WithTransactions()` is called before `WithPropertyChangeObservable()` or `WithPropertyChangeQueue()`

4. **Handle exceptions from CommitAsync**: Especially when using external sources, commits can fail partially

5. **Don't share transactions across threads**: Each async context should have its own transaction

## API Reference

### SubjectTransaction

| Member | Description |
|--------|-------------|
| `static Current` | Gets the current transaction in the execution context, or `null` |
| `static BeginTransaction()` | Begins a new transaction |
| `GetPendingChanges()` | Returns the list of pending property changes |
| `CommitAsync(ct)` | Commits all pending changes (resolves write callbacks from each subject's context) |
| `Dispose()` | Discards uncommitted changes and clears the current transaction |

### SubjectTransactionInterceptor

| Member | Description |
|--------|-------------|
| `WriteChangesCallback` | Optional callback for writing changes to external sources |
