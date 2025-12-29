# Authorization

HomeBlaze includes a role-based authorization system that controls access to subject properties and methods.

## Overview

The authorization system provides:

- **Role-based access control** with a built-in role hierarchy
- **Two-dimensional permissions**: Entity type (State/Configuration/Query/Operation) + Action (Read/Write/Invoke)
- **Attribute-based configuration** for subjects, properties, and methods
- **Runtime overrides** through the UI for per-subject customization
- **Inheritance** from parent subjects in the object graph

## Roles

HomeBlaze uses a hierarchical role system where higher roles automatically include permissions of lower roles:

| Role | Description |
|------|-------------|
| **Admin** | Full access including user management |
| **Supervisor** | Can modify configuration settings |
| **Operator** | Can modify state and invoke operations |
| **User** | Can view configuration and invoke queries |
| **Guest** | Can view state properties |
| **Anonymous** | Unauthenticated users (lowest privilege) |

The hierarchy flows: Admin → Supervisor → Operator → User → Guest → Anonymous

This means an **Operator** automatically has all permissions granted to **User**, **Guest**, and **Anonymous**.

## Entity Types and Actions

Permissions are defined by combining an **Entity** type with an **Action**:

### Entity Types

| Entity | Applies To | Description |
|--------|------------|-------------|
| `State` | Properties with `[State]` | Runtime values (sensor readings, device status) |
| `Configuration` | Properties with `[Configuration]` | Persisted settings |
| `Query` | Methods with `[Query]` | Read-only methods |
| `Operation` | Methods with `[Operation]` | State-changing methods |

### Actions

| Action | Description |
|--------|-------------|
| `Read` | Reading a property value |
| `Write` | Modifying a property value |
| `Invoke` | Executing a method |

## Default Permissions

When no specific permissions are defined, these defaults apply:

| Entity | Action | Default Role |
|--------|--------|--------------|
| State | Read | Guest |
| State | Write | Operator |
| Configuration | Read | User |
| Configuration | Write | Supervisor |
| Query | Invoke | User |
| Operation | Invoke | Operator |

## Configuring Permissions

### Subject-Level Permissions

Use `[SubjectAuthorize]` on a class to set default permissions for all its properties:

```csharp
[InterceptorSubject]
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Read, "Guest")]
[SubjectAuthorize(AuthorizationEntity.State, AuthorizationAction.Write, "Operator")]
[SubjectAuthorize(AuthorizationEntity.Configuration, AuthorizationAction.Write, "Admin")]
public partial class SecurityCamera
{
    [State]
    public partial bool IsRecording { get; set; }

    [Configuration]
    public partial string StreamUrl { get; set; }
}
```

### Property-Level Permissions

Use `[SubjectPropertyAuthorize]` to override permissions for specific properties:

```csharp
[InterceptorSubject]
public partial class SecuritySystem
{
    [Configuration]
    [SubjectPropertyAuthorize(AuthorizationAction.Read, "Admin")]
    [SubjectPropertyAuthorize(AuthorizationAction.Write, "Admin")]
    public partial string ArmCode { get; set; }  // Only admins can see or change

    [State]
    public partial bool IsArmed { get; set; }  // Uses subject/default permissions
}
```

### Method-Level Permissions

Use `[SubjectMethodAuthorize]` to restrict method access:

```csharp
[InterceptorSubject]
public partial class Device
{
    [Operation]
    public void TurnOn() { }  // Uses default Operation permissions (Operator)

    [Operation]
    [SubjectMethodAuthorize("Admin")]
    public async Task FactoryResetAsync() { }  // Only Admin can invoke

    [Query]
    [SubjectMethodAuthorize("Guest", "User")]
    public DeviceStatus GetStatus() { }  // Guest or User can invoke
}
```

## Permission Resolution Order

When determining permissions for a property or method, the system checks in this order:

1. **Runtime override** on the specific property/method
2. **Runtime override** on the subject
3. **Attribute** on the property/method (`[SubjectPropertyAuthorize]` / `[SubjectMethodAuthorize]`)
4. **Attribute** on the subject class (`[SubjectAuthorize]`)
5. **Parent subjects** (combined with OR logic)
6. **Global defaults**

The first match wins and stops the search.

## Runtime Overrides

Permissions can be modified at runtime through the UI without changing code:

1. Open a subject's properties panel
2. Click the lock icon next to a property or method
3. Select "Override permissions"
4. Choose the roles that should have access
5. Optionally enable "Allow child subjects to inherit this override"

Runtime overrides are persisted with the subject configuration.

### Inheritance Flag

When creating a runtime override, you can choose whether child subjects inherit it:

- **Unchecked** (default): Only this subject uses the override; children don't see it
- **Checked**: Child subjects will inherit this permission unless they define their own override

## Parent Inheritance

Subjects can have multiple parents in the object graph. When resolving permissions:

- All parent branches are traversed
- Roles from all branches are combined (OR logic)
- Any role from any parent grants access

Example: A `Light` in both `LivingRoom` (allows Guest) and `Kitchen` (allows Chef) can be accessed by either Guest OR Chef.

## Setup

### 1. Add Authorization Services

In `Program.cs`:

```csharp
// Add authorization with default SQLite database
builder.Services.AddHomeBlazeAuthorization();

// Or with custom connection string
builder.Services.AddHomeBlazeAuthorization("Data Source=myapp.db");
```

### 2. Add Middleware

```csharp
app.UseAuthentication();
app.UseAuthorization();
```

### 3. First Run

On first startup, the system automatically:
- Creates the identity database
- Seeds default roles
- Creates an admin user (requires password setup on first login)

## Admin Pages

The authorization system includes built-in admin pages:

| Page | Path | Description |
|------|------|-------------|
| Login | `/login` | User authentication |
| My Account | `/account` | Change password, view profile |
| Users | `/admin/users` | User management (Admin only) |
| Roles | `/admin/roles` | Role hierarchy configuration (Admin only) |

## Access Denied Handling

When a user attempts an unauthorized action:

- **UI**: Controls are hidden or disabled before the user can interact
- **API/Code**: An `UnauthorizedAccessException` is thrown

The UI filtering is the primary defense; exceptions are a safety net for edge cases.

## Troubleshooting

### User Can't Access Something They Should

1. Check the user's assigned roles in Admin > Users
2. Verify the role hierarchy in Admin > Roles
3. Check if there's a runtime override on the subject
4. Check parent subjects for restrictive overrides

### Permissions Not Taking Effect

- Changes to role hierarchy require the user to log out and back in
- Runtime overrides take effect immediately
- Attribute changes require application restart

## Architecture

This section covers the internal architecture for developers who need to extend or debug the system.

### Interceptor-Based Enforcement

Authorization is enforced through the Namotion.Interceptor pipeline. The `AuthorizationInterceptor` runs first (before other interceptors) and checks permissions on every property read/write:

```
Property Access → AuthorizationInterceptor → [Other Interceptors] → Actual Value
                         ↓
                  Check permissions
                         ↓
              Authorized? → Continue
              Denied? → Throw UnauthorizedAccessException
```

This means authorization cannot be bypassed by accessing properties directly - the interceptor always runs.

### Method Authorization

Methods marked with `[Query]` or `[Operation]` are protected by `AuthorizedMethodInvoker`, which wraps the standard method invoker and checks permissions before execution.

### Resolution Chain

The `IAuthorizationResolver` determines required roles using this chain:

```
1. Property runtime override (Subject.Data)     → STOP if found
2. Subject runtime override (Subject.Data)      → STOP if found
3. [SubjectPropertyAuthorize] attribute         → STOP if found
4. [SubjectAuthorize] attribute                 → STOP if found
5. Parent traversal (OR combine all branches)   → STOP if found
6. Global defaults                              → Always returns something
```

For methods, step 3 uses `[SubjectMethodAuthorize]` instead.

### Parent Traversal Algorithm

When traversing parents:

1. Check each parent's runtime override (only if `Inherit = true`)
2. Check each parent's `[SubjectAuthorize]` attribute
3. If neither found, recurse to grandparents
4. Collect roles from all branches (OR semantics)
5. Stop each branch at the first ancestor with defined roles

### Runtime Override Storage

Overrides are stored in `Subject.Data` with composite keys:

- Key format: `(propertyName, "HomeBlaze.Authorization:{Entity}:{Action}")`
- Subject-level overrides use `null` for propertyName
- Value: `AuthorizationOverride { Inherit, Roles[] }`

### JSON Persistence

Runtime overrides are serialized as `$authorization` in the subject JSON:

```json
{
  "$type": "MyDevice",
  "name": "Living Room Light",
  "$authorization": {
    "": {
      "State:Write": { "inherit": false, "roles": ["Admin"] }
    },
    "ApiKey": {
      "Configuration:Read": { "inherit": false, "roles": ["Admin"] },
      "Configuration:Write": { "inherit": false, "roles": ["Admin"] }
    }
  }
}
```

- Empty string `""` = subject-level override
- Property name = property-level override
- `inherit: true` = children see this override when traversing parents
- `inherit: false` = children skip this override, continue to grandparents

### Key Services

| Service | Lifetime | Purpose |
|---------|----------|---------|
| `IAuthorizationResolver` | Singleton | Resolves required roles (with caching) |
| `IRoleExpander` | Singleton | Expands role hierarchy |
| `AuthorizationDataProvider` | Singleton | Serializes `$authorization` to/from JSON |

### Cache Invalidation

The resolver caches resolved permissions per (subject, property, entity, action). Call `InvalidateCache(subject)` after modifying runtime overrides programmatically.

## See Also

- [Architecture](Architecture.md) - System architecture overview
- [BuildingSubjects](BuildingSubjects.md) - Creating custom subjects
- [Configuration](Configuration.md) - Configuration system
