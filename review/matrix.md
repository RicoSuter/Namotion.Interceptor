# OPC UA Graph Sync - Test Coverage Matrix

## Permutation Matrix

| Direction | Data Type | Operation | Test Coverage | Test File |
|-----------|-----------|-----------|---------------|-----------|
| **Server → Client** | Reference | Assign | ✅ | `ServerToClientReferenceTests` |
| **Server → Client** | Reference | Clear | ✅ | `ServerToClientReferenceTests` |
| **Server → Client** | Reference | Replace | ✅ | `ServerToClientReferenceTests` |
| **Server → Client** | Collection (Container) | Add | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Container) | Remove | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Container) | Remove Middle (Reindex) | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Container) | Sequential Add/Remove | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Container) | Move | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Flat) | Add | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Collection (Flat) | Remove | ✅ | `ServerToClientCollectionTests` |
| **Server → Client** | Dictionary | Add | ✅ | `ServerToClientDictionaryTests` |
| **Server → Client** | Dictionary | Remove | ✅ | `ServerToClientDictionaryTests` |
| **Server → Client** | Dictionary | Replace | ✅ | `ServerToClientDictionaryTests` |
| **Server → Client** | Dictionary | Sequential | ✅ | `ServerToClientDictionaryTests` |
| **Server → Client** | Nested Property | Modify | ✅ | `ValueSyncNestedTests` |
| **Client → Server** | Reference | Assign | ✅ | `ClientToServerReferenceTests` |
| **Client → Server** | Reference | Clear | ✅ | `ClientToServerReferenceTests` |
| **Client → Server** | Reference | Replace | ✅ | `ClientToServerReferenceTests` |
| **Client → Server** | Collection (Container) | Add | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Container) | Remove | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Container) | Index Tracking | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Container) | Move | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Flat) | Add | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Flat) | Remove | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Collection (Flat) | Move | ✅ | `ClientToServerCollectionTests` |
| **Client → Server** | Dictionary | Add | ✅ | `ClientToServerDictionaryTests` |
| **Client → Server** | Dictionary | Remove | ✅ | `ClientToServerDictionaryTests` |
| **Client → Server** | Dictionary | Replace | ✅ | `ClientToServerDictionaryTests` |
| **Client → Server** | Nested Property (Collection) | Modify | ✅ | `ClientToServerNestedPropertyTests` |
| **Client → Server** | Nested Property (Dictionary) | Modify | ✅ | `ClientToServerNestedPropertyTests` |
| **Client → Server** | Nested Property (Reference) | Modify | ✅ | `ClientToServerNestedPropertyTests` |

## Additional Scenarios

| Scenario | Test Coverage | Test File |
|----------|---------------|-----------|
| Periodic Resync (polling mode) | ✅ | `PeriodicResyncTests` (6 tests) |
| External Node Management (Server accepts AddNodes/DeleteNodes) | ✅ | `ServerExternalManagementTests` |
| Remote Node Creation (Client calls AddNodes) | Partial | Tested via ClientToServer* tests when `EnableGraphChangePublishing=true` |

## Known Limitations

| Limitation | Description |
|------------|-------------|
| Deep Nested Object Creation (Client → Server) | When client sets `person.Address = new NestedAddress(...)`, it doesn't sync to server. Server → Client works. |
