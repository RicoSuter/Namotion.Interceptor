# OPC UA Integration Tests Implementation Plan

> **Status: IMPLEMENTED** - 13 new tests added, all passing.

**Goal:** Add integration tests covering data types, collection edge cases, bidirectional nested sync, transaction rollback, and multi-client scenarios using the shared OPC UA server infrastructure.

**Architecture:** Each test area gets its own isolated section in `SharedTestModel` to prevent interference. Tests follow the existing pattern: inherit from `SharedServerTestBase`, use `AsyncTestHelpers.WaitUntilAsync()` for synchronization.

**Tech Stack:** xUnit, C# 13 partial properties, `[InterceptorSubject]` source generation, OPC UA client/server

---

## Implementation Status

| Task | Description | Planned | Implemented | Status |
|------|-------------|---------|-------------|--------|
| 1-2 | Data Types Tests | 5 tests (bool, int/long, DateTime, byte[], Guid) | 4 tests (Guid removed) | Done |
| 3-4 | Collection Tests | 4 tests (arrays, dictionaries) | 2 tests (dictionaries removed) | Done |
| 5 | Bidirectional Nested Tests | 1 test | 1 test | Done |
| 6 | Transaction Rollback Tests | 2 tests (dispose, cancel) | 2 tests (both dispose-based) | Done |
| 7-8 | Multi-Client Tests | 4 tests | 4 tests | Done |
| 9 | Final verification | - | - | Done |

**Total: 13 new integration tests implemented (all passing)**

---

## Files Modified

### New Test Files
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaDataTypesTests.cs` - 4 tests
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaCollectionEdgeCaseTests.cs` - 2 tests
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaBidirectionalNestedTests.cs` - 1 test
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaTransactionRollbackTests.cs` - 2 tests
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/OpcUaMultiClientTests.cs` - 4 tests

### Modified Test Infrastructure
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/SharedTestModel.cs` - Added DataTypes, Collections, MultiClient test areas
- `src/Namotion.Interceptor.OpcUa.Tests/Integration/Testing/SharedOpcUaServerFixture.cs` - Added initialization for new test areas

---

## Findings

### BUG: Guid Type Breaks All OPC UA Subscriptions
- **Severity:** High
- **Issue:** Adding a `Guid` property to the test model caused ALL OPC UA tests to fail with timeout errors, not just tests using the Guid property.
- **Root Cause:** Unknown - needs investigation. A single unsupported property type breaks the entire subscription mechanism.
- **Resolution:** Removed `GuidValue` property from implementation.
- **Follow-up Required:** Investigate why a single property with an unsupported type breaks all subscriptions rather than failing gracefully. This is a bug in the OPC UA server/client implementation.

### Test Design Error: Primitive Dictionary Not Supported
- **Issue:** Tests expected `Dictionary<string, string>` to sync via OPC UA client writes.
- **Actual Behavior:** Primitive dictionaries cannot be stored in OPC UA Variant objects. Only dictionary-of-subjects (object nodes with child references) is supported.
- **Resolution:** Removed dictionary tests. Array tests retained.
- **Note:** Dictionary structure cannot change dynamically (see PR #121 for structural sync).

### Test Design Error: Transaction Cancellation Token
- **Issue:** Original test assumed `CommitAsync(cancellationToken)` would cancel the commit if token was cancelled.
- **Actual Behavior:** The `cancellationToken` parameter is only used for acquiring the optimistic lock. The actual write uses an internal timeout from `CreateCommitTimeoutCts()`.
- **Resolution:** Replaced with dispose-without-commit test which correctly tests rollback behavior.
- **Conclusion:** Not a bug - test misunderstood the API design.

### Test Infrastructure Issue: Parallel Test Interference
- **Issue:** `OpcUaStallDetectionTests` intentionally restart the OPC UA server, causing other parallel tests to fail with connection errors.
- **Symptoms:** Tests fail with `Failed to discover OPC UA endpoints` during server restart.
- **Root Cause:** Shared server architecture + tests that restart the server.
- **Mitigation:** Added detailed logging. New tests use isolated data areas.
- **Follow-up:** Consider using `[Collection]` attribute to serialize server-restarting tests.

### Clarification: Transactions Not Required for Sync
- **Clarification:** Properties sync to server without explicit transactions. `.WithSourceTransactions()` enables optional atomic behavior for multi-property changes.
- **Impact:** Simplified tests to not require transactions for basic sync verification.

---

## Test Results

```
All new tests: 13/13 passing
- OpcUaDataTypesTests: 4 passing
- OpcUaCollectionEdgeCaseTests: 2 passing
- OpcUaBidirectionalNestedTests: 1 passing
- OpcUaTransactionRollbackTests: 2 passing
- OpcUaMultiClientTests: 4 passing

Overall integration tests: 25/27 passing
- 2 failures in OpcUaStallDetectionTests (pre-existing test infrastructure issue)
```

---

## Original Plan (for reference)

<details>
<summary>Click to expand original planned tasks</summary>

### Task 1-2: DataTypes Tests (Originally 5 tests)
- BooleanType sync
- IntegerTypes (int/long) sync
- DateTimeType sync
- ByteArrayType sync
- ~~GuidType sync~~ (removed - breaks OPC UA)

### Task 3-4: Collection Tests (Originally 4 tests)
- EmptyArray sync
- ArrayResize sync
- ~~DictionaryOperations sync~~ (removed - not supported)
- ~~NullableDictionary sync~~ (removed - not supported)

### Task 5: Bidirectional Nested Tests (1 test)
- NestedPropertyChanges client→server

### Task 6: Transaction Rollback Tests (Originally 2 tests)
- Transaction disposed without commit
- ~~Transaction cancelled during commit~~ (replaced with multi-property dispose test)

### Task 7-8: Multi-Client Tests (4 tests)
- Client1 writes, server receives
- Client2 writes, server receives
- Both clients write different properties
- Sequential writes, last write wins

</details>
