# Potential Cleanup/Refactoring Opportunities

## Known Limitations

### Deep Nested Object Creation (Client → Server)
When the client creates a new nested object on an existing subject (e.g., `person.Address = new NestedAddress(...)`), it doesn't sync to the server. This would require the client to call AddNodes for nested objects, which is not yet implemented. Server → Client deep nesting works (tested in `ValueSyncNestedTests`).

---

## 1. Large File: `OpcUaClientGraphChangeReceiver.cs` (1343 lines)

Could split into:
- `ModelChangeEventProcessor` - handles NodeAdded/NodeDeleted/ReferenceAdded/ReferenceDeleted
- `PeriodicResyncProcessor` - handles full resync
- Keep receiver as orchestrator

## 2. Duplicated Browse/Navigation Logic

`OpcUaHelper` has good utilities but some duplicated patterns exist in:
- `OpcUaClientGraphChangeSender.TryFindChildNodeAsync`
- `OpcUaServerGraphChangeReceiver.TryAddSubjectToParent`

Consider consolidating browse/find patterns into `OpcUaHelper`.

## 3. Source Filtering Inconsistency

Comments say "Source filtering is handled by ChangeQueueProcessor" but `SubjectChangeContext.Current.Source` is also checked in some places. Could consolidate to one approach.

## 4. Collection Index Management

Both client and server have logic for parsing `PropertyName[index]` pattern. `UpdateCollectionNodeIdRegistrationsAfterRemoval` has manual string manipulation. Could centralize in `OpcUaHelper`.

## 5. Test Helpers Duplication

`SharedServerTestBase` vs dedicated server tests (`PeriodicResyncTests`) have similar setup/teardown patterns that could be further consolidated.
