# HomeBlaze Authorization Design

**Date**: 2025-12-25
**Status**: Draft

## Overview

Add authentication and authorization to the HomeBlaze Blazor application with:
- Local accounts + configurable OAuth providers
- Composable roles with external provider mapping
- Subject-level and property-level access control
- Multi-parent inheritance with OR semantics

---

## 1. Authentication Layer

### Architecture

```
┌─────────────────────────────────────────────────────┐
│ ASP.NET Core Identity + Configurable OAuth          │
├─────────────────────────────────────────────────────┤
│ • Local accounts (default)                          │
│ • OAuth providers via appsettings (optional)        │
│ • SQLite storage (swappable to PostgreSQL)          │
│ • User record always exists (with or without pwd)   │
└─────────────────────────────────────────────────────┘
```

### Configuration

**appsettings.json:**
```json
{
  "Authentication": {
    "OAuth": {
      "Google": { "ClientId": "...", "ClientSecret": "..." },
      "Microsoft": { "ClientId": "...", "ClientSecret": "..." }
    }
  }
}
```

- OAuth providers only registered if config exists
- No config = local accounts only
- User records created for OAuth users on first login (no password)

---

## 2. Roles

### Assignment

Roles are flat strings assigned to users:

```
User "alice@example.com" → Roles: ["Admin"]
User "bob@example.com"   → Roles: ["User", "SecurityGuard"]
User "guest@example.com" → Roles: ["Guest"]
```

### Composition

Roles can include other roles (OR combination). Role hierarchy is **stored in database** (not appsettings.json) and is editable via Admin UI:

```
Database: RoleComposition table
┌────────────┬──────────────────┐
│ RoleName   │ IncludesRole     │
├────────────┼──────────────────┤
│ Guest      │ (none)           │
│ User       │ Guest            │
│ SecurityGuard │ Guest         │
│ HomeOwner  │ User             │
│ Admin      │ HomeOwner        │
│ Admin      │ SecurityGuard    │
└────────────┴──────────────────┘
```

**Expansion:**
```
Admin → [Admin, HomeOwner, User, Guest, SecurityGuard]
User  → [User, Guest]
Guest → [Guest]
```

### External Role Mapping

Map OAuth provider roles/groups to internal roles:

```json
{
  "Authorization": {
    "ExternalRoleMappings": {
      "Google": {
        "admins@mydomain.com": "Admin"
      },
      "Microsoft": {
        "HomeBlaze-Admins": "Admin"
      }
    }
  }
}
```

---

## 3. Authorization Model

Authorization uses two dimensions: **Kind** (what type of member) and **Action** (what operation).

### AuthorizationAction Enum

```csharp
public enum AuthorizationAction
{
    Read,    // Read property value
    Write,   // Write property value
    Invoke   // Invoke method
}
```

### Kind Enum

```csharp
public enum Kind
{
    State,          // Runtime state properties
    Configuration,  // Persisted configuration properties
    Query,          // Read-only methods (idempotent)
    Operation       // Mutating methods (side effects)
}
```

### Default Roles

```csharp
public static class DefaultRoles
{
    public const string Guest = "Guest";
    public const string User = "User";
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Admin = "Admin";
}
```

---

## 4. Property/Method Kind Markers

Properties and methods are marked with their **Kind**:

```csharp
[InterceptorSubject]
public partial class Light
{
    [State]  // Runtime state
    public partial bool IsOn { get; set; }

    [Configuration]  // Persisted setting
    public partial string DisplayName { get; set; }

    [Query]  // Read-only method
    public LightStatus GetStatus() { }

    [Operation]  // Mutating method
    public void Toggle() { }
}
```

The Kind determines which global defaults apply and how authorization attributes are resolved.

---

## 5. Authorization Attributes

Three attributes for specifying role requirements:

### `[SubjectAuthorize]` - On Subject Class

Applies to all properties/methods of matching Kind + Action:

```csharp
[SubjectAuthorize(Kind.State, AuthorizationAction.Read, DefaultRoles.Guest)]
[SubjectAuthorize(Kind.State, AuthorizationAction.Write, DefaultRoles.User)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Read, DefaultRoles.Operator)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Write, DefaultRoles.Admin)]
[SubjectAuthorize(Kind.Query, AuthorizationAction.Invoke, DefaultRoles.Guest)]
[SubjectAuthorize(Kind.Operation, AuthorizationAction.Invoke, DefaultRoles.User)]
public partial class Light { }
```

### `[SubjectPropertyAuthorize]` - On Property

Overrides subject-level for this property. Kind is implicit from `[State]`/`[Configuration]`:

```csharp
[Configuration]
[SubjectPropertyAuthorize(AuthorizationAction.Read, DefaultRoles.Guest, DefaultRoles.User)]
[SubjectPropertyAuthorize(AuthorizationAction.Write, DefaultRoles.Admin)]
public partial string DisplayName { get; set; }
```

### `[SubjectMethodAuthorize]` - On Method

Overrides subject-level for this method. Kind is implicit from `[Query]`/`[Operation]`, Action is always Invoke:

```csharp
[Operation]
[SubjectMethodAuthorize(DefaultRoles.Admin)]
public void FactoryReset() { }
```

---

## 6. Runtime Overrides

Override role mappings at runtime via extension data:

### Subject-Level Override

```csharp
// Override for all State+Read on this subject
subject.SetRoles(Kind.State, AuthorizationAction.Read, ["SecurityGuard"], inherit: true);
```

### Property-Level Override

```csharp
// Override for this specific property
var property = subject.GetProperty("ApiKey");
property.SetRoles(AuthorizationAction.Read, ["Admin"]);  // Kind from property's marker
```

### Inheritance

When `inherit: true`, the override applies to child subjects unless they define their own override.

---

## 7. Resolution Priority

For a property with Kind `K` and Action `A`:

```
1. Property runtime override (Kind, AuthorizationAction)           → use it, STOP
        ↓ not found
2. Subject runtime override (Kind, AuthorizationAction)            → use it, STOP
        ↓ not found
3. Property [SubjectPropertyAuthorize(Action)]        → use it, STOP
        ↓ not found
4. Subject [SubjectAuthorize(Kind, AuthorizationAction)]           → use it, STOP
        ↓ not found
5. Parent inheritance                                 → OR combine
        ↓ none found
6. Global defaults (Kind, AuthorizationAction)                     → use it
```

For methods, Kind is from `[Query]`/`[Operation]` and Action is always `Invoke`.

---

## 8. Multi-Parent Inheritance

Subjects can have multiple parents (graph structure).

### Example

```
          Home
         /    \
   LivingRoom  Kitchen
   (State+Read→[Guest])  (State+Read→[Chef])
         \    /
          Light ← TWO parents
```

### Traversal Rules

1. Traverse ALL parent branches
2. Stop at first ancestor with roles defined for (Kind, AuthorizationAction)
3. That ancestor contributes its roles
4. **OR combine** across all branches
5. Defining roles = override point (stops further traversal up that branch)

### Result

- LivingRoom: `State+Read→[Guest]` → stops, contributes [Guest]
- Kitchen: `State+Read→[Chef]` → stops, contributes [Chef]
- **Result: [Guest] OR [Chef]** (either role grants access)

---

## 9. Role Matching

OR semantics - user needs ANY of the required roles:

| Property Requires | User Has | Access |
|-------------------|----------|--------|
| [Admin, SecurityGuard] | [Admin] | ✓ |
| [Admin, SecurityGuard] | [SecurityGuard] | ✓ |
| [Admin, SecurityGuard] | [Guest] | ✗ |
| [Admin, SecurityGuard] | [Guest, Admin] | ✓ |

---

## 10. Resolution Algorithm

```
ResolvePropertyRoles(property, action):
  kind = property.Kind  // From [State] or [Configuration] marker

  1. property.RuntimeOverride(kind, action)?              → return it
  2. subject.RuntimeOverride(kind, action)?               → return it
  3. property.GetAttribute<SubjectPropertyAuthorize>(action)? → return it
  4. subject.GetAttribute<SubjectAuthorize>(kind, action)?    → return it
  5. TraverseParents(subject, kind, action)               → OR combine
  6. GlobalDefaults(kind, action)                         → return it

ResolveMethodRoles(method):
  kind = method.Kind  // From [Query] or [Operation] marker
  action = AuthorizationAction.Invoke

  1. method.RuntimeOverride(kind, action)?                → return it
  2. subject.RuntimeOverride(kind, action)?               → return it
  3. method.GetAttribute<SubjectMethodAuthorize>()?       → return it
  4. subject.GetAttribute<SubjectAuthorize>(kind, action)?    → return it
  5. TraverseParents(subject, kind, action)               → OR combine
  6. GlobalDefaults(kind, action)                         → return it

TraverseParents(subject, kind, action):
  roles = []
  for each parent in subject.Parents:
    if parent.RuntimeOverride(kind, action) OR parent.Attribute(kind, action):
      roles = roles OR (parent.RuntimeOverride ?? parent.Attribute)
      // STOP this branch
    else:
      roles = roles OR TraverseParents(parent, kind, action)
  return roles
```

---

## 11. Data Storage

Using Namotion.Interceptor extension data with `HomeBlaze.Authorization:` prefix:

```csharp
// Subject-level override (Kind+Action as key) - use null for subject-level
subject.Data[(null, "HomeBlaze.Authorization:State:Read")] = new[] { "Guest" };
subject.Data[(null, "HomeBlaze.Authorization:Configuration:Write")] = new[] { "Admin" };

// Property-level override (Kind determined from property's [State]/[Configuration] attribute)
subject.Data[("ApiKey", "HomeBlaze.Authorization:Configuration:Read")] = new[] { "Admin" };
```

---

## 12. Full Example

```
Subject Graph:
──────────────
Home
├── [SubjectAuthorize(State, Read, Guest)]
├── [SubjectAuthorize(State, Write, User)]
├── [SubjectAuthorize(Configuration, Read, Operator)]
├── [SubjectAuthorize(Configuration, Write, Admin)]
│
├── LivingRoom
│   └── (no override, inherits from Home)
│   │
│   └── Light1
│       ├── IsOn [State]                    → State+Read inherits [Guest]
│       ├── DisplayName [Configuration]    → Config+Read inherits [Operator]
│       └── Toggle() [Operation]           → Operation+Invoke inherits [User]
│
└── SecuritySystem
    ├── RuntimeOverride: State+Read→[SecurityGuard]  ← OVERRIDE
    │
    └── Camera
        ├── IsRecording [State]              → State+Read inherits [SecurityGuard]
        ├── ApiKey [Configuration]           → Config+Read traverses to Home → [Operator]
        └── FactoryReset() [Operation]       → Operation+Invoke inherits [User]
            └── [SubjectMethodAuthorize(Admin)]  ← OVERRIDE
```

**Access Check: `Camera.IsRecording` (State+Read) with user roles ["Guest"]:**
1. Property runtime override (State, Read)? No
2. Subject (Camera) runtime override (State, Read)? No
3. Property [SubjectPropertyAuthorize(Read)]? No
4. Subject (Camera) [SubjectAuthorize(State, Read)]? No
5. Traverse → SecuritySystem has State+Read→[SecurityGuard]
6. Required: [SecurityGuard], User has: [Guest] → **DENIED**

**Access Check: `Camera.FactoryReset()` (Operation+Invoke) with user roles ["User"]:**
1. Method runtime override? No
2. Subject (Camera) runtime override (Operation, Invoke)? No
3. Method [SubjectMethodAuthorize]? Yes → [Admin]
4. Required: [Admin], User has: [User] → **DENIED**

---

## 13. Database Schema (Identity)

```sql
-- ASP.NET Core Identity tables
AspNetUsers
AspNetRoles
AspNetUserRoles
AspNetUserClaims
AspNetUserLogins      -- OAuth provider links
AspNetUserTokens

-- Custom extensions
UserProfiles (optional)
├── UserId (FK)
├── DisplayName
├── CreatedAt
├── LastLoginAt
```

---

## 14. Project Structure

```
src/HomeBlaze/
├── HomeBlaze.Authorization/
│   ├── Configuration/
│   │   ├── AuthorizationOptions.cs
│   │   └── DefaultRoles.cs
│   ├── Enums/
│   │   ├── Action.cs
│   │   └── Kind.cs
│   ├── Roles/
│   │   ├── RoleExpander.cs
│   │   └── ExternalRoleMapper.cs
│   ├── Resolution/
│   │   ├── IAuthorizationResolver.cs
│   │   ├── AuthorizationResolver.cs
│   │   └── ResolvedRoles.cs
│   ├── Attributes/
│   │   ├── SubjectAuthorizeAttribute.cs
│   │   ├── SubjectPropertyAuthorizeAttribute.cs
│   │   └── SubjectMethodAuthorizeAttribute.cs
│   ├── Extensions/
│   │   └── SubjectAuthorizationExtensions.cs
│   └── ServiceCollectionExtensions.cs
│
├── HomeBlaze.Authorization.Blazor/
│   ├── Panes/
│   │   ├── SubjectAuthorizationPane.razor
│   │   ├── PropertyAuthorizationPane.razor
│   │   └── MethodAuthorizationPane.razor
│   ├── Components/
│   │   ├── AuthorizationButton.razor
│   │   ├── PropertyAuthIcon.razor
│   │   ├── MethodAuthIcon.razor
│   │   ├── KindActionTable.razor
│   │   └── RoleSelector.razor
│   └── ServiceCollectionExtensions.cs
│
└── HomeBlaze/
    ├── Data/
    │   └── ApplicationDbContext.cs
    └── Program.cs (updated with auth middleware)
```

---

## 15. UI Design

### UI Components Overview

```
┌─────────────────────────────────────────────────────────────┐
│ 1. User Management Page        (Admin only)                │
│    - List users, assign roles                              │
│                                                             │
│ 2. Role Configuration Page     (Admin only)                │
│    - Define role composition                               │
│    - Configure external role mappings                      │
│                                                             │
│ 3. Subject Authorization Panel (inline, per subject)       │
│    - View/edit subject-level role mappings                 │
│    - View/edit property-level overrides                    │
│    - Shows inheritance source                              │
└─────────────────────────────────────────────────────────────┘
```

### User Management Page

```
┌─────────────────────────────────────────────────────────────┐
│ Users                                            [+ Add]    │
├─────────────────────────────────────────────────────────────┤
│ ┌─────────────────┬──────────────┬──────────────┬────────┐ │
│ │ Email           │ Provider     │ Roles        │ Actions│ │
│ ├─────────────────┼──────────────┼──────────────┼────────┤ │
│ │ alice@email.com │ Local        │ Admin        │ ✏️ ️  │ │
│ │ bob@email.com   │ Google       │ User         │ ✏️ ️  │ │
│ │ carol@work.com  │ Microsoft    │ SecurityGuard│ ✏️ ️  │ │
│ └─────────────────┴──────────────┴──────────────┴────────┘ │
└─────────────────────────────────────────────────────────────┘
```

**Edit User Dialog:**
```
┌─────────────────────────────────────┐
│ Edit User: bob@email.com            │
├─────────────────────────────────────┤
│ Display Name: [Bob Smith        ]   │
│                                     │
│ Roles:                              │
│ ☑ Admin                             │
│ ☐ HomeOwner                         │
│ ☑ User                              │
│ ☐ SecurityGuard                     │
│ ☐ Guest                             │
│                                     │
│        [Cancel]  [Save]             │
└─────────────────────────────────────┘
```

### Role Configuration Page

```
┌─────────────────────────────────────────────────────────────┐
│ Roles                                            [+ Add]    │
├─────────────────────────────────────────────────────────────┤
│ ┌──────────────┬────────────────────────────────┬────────┐ │
│ │ Role         │ Includes                       │ Actions│ │
│ ├──────────────┼────────────────────────────────┼────────┤ │
│ │ Guest        │ (base role)                    │ ✏️ ️  │ │
│ │ User         │ Guest                          │ ✏️ ️  │ │
│ │ SecurityGuard│ Guest                          │ ✏️ ️  │ │
│ │ HomeOwner    │ User                           │ ✏️ ️  │ │
│ │ Admin        │ HomeOwner, SecurityGuard       │ ✏️ ️  │ │
│ └──────────────┴────────────────────────────────┴────────┘ │
│                                                             │
│ External Role Mappings                                      │
│ ┌──────────────┬─────────────────────┬───────────┬───────┐ │
│ │ Provider     │ External Role       │ Maps To   │Actions│ │
│ ├──────────────┼─────────────────────┼───────────┼───────┤ │
│ │ Google       │ admins@domain.com   │ Admin     │ ✏️ ️ │ │
│ │ Microsoft    │ HomeBlaze-Admins    │ Admin     │ ✏️ ️ │ │
│ │ Microsoft    │ HomeBlaze-Users     │ User      │ ✏️ ️ │ │
│ └──────────────┴─────────────────────┴───────────┴───────┘ │
└─────────────────────────────────────────────────────────────┘
```

### Subject Pane with Authorization Button

The existing subject pane gets a new `[🔒 Auth]` button alongside Edit/Delete:

```
┌─────────────────────────────────────────────────────────────┐
│ SecuritySystem                       [🔒 Auth] [✏️] [🗑️]    │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ Properties                                                  │
│ ┌──────────────┬─────────────────────────────────────┬────┐ │
│ │ IsRecording  │ true                                │ 🔒 │ │
│ │ MacAddress   │ AA:BB:CC:DD:EE:FF                  │ 🔒*│ │  ← * = has override
│ │ LastMotion   │ 2025-12-26T10:30:00                │ 🔒 │ │
│ └──────────────┴─────────────────────────────────────┴────┘ │
│                                                             │
│ Methods                                                     │
│ [Start Recording] [🔒]    [Factory Reset] [🔒*]             │  ← 🔒* = has override
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Subject Authorization Pane (Right Pane)

Click `[🔒 Auth]` → Opens right pane with subject-level permissions:

```
┌─────────────────────────────────────────────────────────────┐
│ Authorization: SecuritySystem                    [Save] [X] │
├─────────────────────────────────────────────────────────────┤
│ Subject Permissions                                         │
│ ┌──────────────────┬─────────────────┬──────────┬─────────┐ │
│ │ Kind + Action    │ Roles           │ Source   │ Actions │ │
│ ├──────────────────┼─────────────────┼──────────┼─────────┤ │
│ │ State+Read       │ SecurityGuard   │ Override │ ✏️ ↩️   │ │
│ │ State+Write      │ Admin           │ Override │ ✏️ ↩️   │ │
│ │ Config+Read      │ Operator        │ Inherited│         │ │
│ │ Config+Write     │ Admin           │ Attribute│         │ │
│ │ Operation+Invoke │ User            │ Default  │         │ │
│ │ Query+Invoke     │ Guest           │ Default  │         │ │
│ └──────────────────┴─────────────────┴──────────┴─────────┘ │
│                                                             │
│ Inheritance source: Home → LivingRoom                       │
│                                                             │
│ Legend:                                                     │
│   Override  = Runtime override (ExtData)                    │
│   Inherited = From parent subject                           │
│   Attribute = [SubjectAuthorize] on class                   │
│   Default   = Global defaults                               │
└─────────────────────────────────────────────────────────────┘
```

### Property Authorization Pane (Right Pane)

Click `🔒` on property → Opens right pane for that property:

```
┌─────────────────────────────────────────────────────────────┐
│ Authorization: MacAddress                        [Save] [X] │
├─────────────────────────────────────────────────────────────┤
│ Kind: Configuration                                         │
│                                                             │
│ Read                                                        │
│ ├─ Current: [Operator, Admin]                               │
│ ├─ Source: Inherited from SecuritySystem                    │
│ └─ ☐ Override: [select roles...]                            │
│                                                             │
│ Write                                                       │
│ ├─ Current: [Admin]                                         │
│ ├─ Source: Override                           [Clear ↩️]    │
│ └─ ☑ Override: [Admin ▼]                                    │
│                                                             │
│ Inheritance Chain:                                          │
│   MacAddress (this) → SecuritySystem → Home                 │
└─────────────────────────────────────────────────────────────┘
```

### Method Authorization Pane (Right Pane)

Click `🔒` next to method button → Opens right pane:

```
┌─────────────────────────────────────────────────────────────┐
│ Authorization: FactoryReset                      [Save] [X] │
├─────────────────────────────────────────────────────────────┤
│ Kind: Operation                                             │
│                                                             │
│ Invoke                                                      │
│ ├─ Current: [Admin]                                         │
│ ├─ Source: [SubjectMethodAuthorize] Attribute               │
│ └─ ☐ Override: [select roles...]                            │
│                                                             │
│ Inheritance Chain:                                          │
│   FactoryReset (this) → SecuritySystem → Home               │
└─────────────────────────────────────────────────────────────┘
```

### Visual Indicators

- `🔒` = Authorization icon (click to open pane)
- `🔒*` or filled icon = Has override (differs from inherited/attribute)
- Properties/methods with overrides get subtle highlight in grid

### Inheritance Visualization

Shown inline in authorization panes:

```
Inheritance Chain:
┌─────────────────────────────────────────────────────────────┐
│ Camera (this)                                               │
│   └── No override                                           │
│       ↓                                                     │
│ SecuritySystem (parent)                                     │
│   └── State+Read: [SecurityGuard] ← RESOLVED                │
│       (stops here)                                          │
│                                                             │
│ Home (grandparent)                                          │
│   └── State+Read: [Guest] (not reached)                     │
└─────────────────────────────────────────────────────────────┘
```

### UI Component Structure

```
HomeBlaze.Authorization.Blazor/
├── Pages/
│   ├── UserManagement.razor          # Admin: manage users and roles
│   └── RoleConfiguration.razor       # Admin: role hierarchy & external mappings
│
├── Panes/
│   ├── SubjectAuthorizationPane.razor    # Right pane for subject-level auth
│   ├── PropertyAuthorizationPane.razor   # Right pane for property auth
│   └── MethodAuthorizationPane.razor     # Right pane for method auth
│
├── Components/
│   ├── AuthorizationButton.razor         # 🔒 button for subject header
│   ├── PropertyAuthIcon.razor            # 🔒 icon for property grid
│   ├── MethodAuthIcon.razor              # 🔒 button next to method buttons
│   ├── KindActionTable.razor             # Table showing Kind+Action permissions
│   ├── RoleSelector.razor                # Multi-select for roles
│   ├── InheritanceChainView.razor        # Visual inheritance chain
│   └── SourceBadge.razor                 # Override/Inherited/Attribute/Default badge
│
└── Services/
    └── AuthorizationUIService.cs         # UI helpers for authorization
```

---

## 16. Unauthenticated Access

### Anonymous Role Mapping

Unauthenticated users are mapped to a configurable role:

```json
{
  "Authorization": {
    "UnauthenticatedRole": "Anonymous"
  }
}
```

### Role Composition

```json
{
  "Authorization": {
    "Roles": {
      "Anonymous": [],
      "Guest": ["Anonymous"],
      "User": ["Guest"],
      "Operator": ["User"],
      "Supervisor": ["Operator"],
      "Admin": ["Supervisor"]
    }
  }
}
```

### Resolution Flow

```
User authenticated?
├── Yes → Use user's roles (expanded)
└── No  → Use UnauthenticatedRole ("Anonymous")
```

### Subject Access for Unauthenticated

```csharp
// Public read access for state
subject.SetRoles(Kind.State, AuthorizationAction.Read, ["Anonymous"]);

// Requires login for write
subject.SetRoles(Kind.State, AuthorizationAction.Write, ["User"]);
```

---

## 17. Default Admin Seeding

### Behavior

On first startup, if no users exist, seed a default admin:

- **Username:** `admin`
- **Password:** empty (none)
- **Must change password:** true

### Implementation

```csharp
public class AdminSeeder : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!await userManager.Users.AnyAsync())
        {
            var admin = new ApplicationUser
            {
                UserName = "admin",
                Email = "admin@localhost",
                MustChangePassword = true
            };

            await userManager.CreateAsync(admin);
            await userManager.AddToRoleAsync(admin, "Admin");

            logger.LogWarning("Default admin created: 'admin' (no password, must set on first login)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
```

### ApplicationUser Extension

```csharp
public class ApplicationUser : IdentityUser
{
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public bool MustChangePassword { get; set; }
}
```

---

## 18. Login UI Flow

### Login Page

```
┌─────────────────────────────────────┐
│           HomeBlaze                 │
├─────────────────────────────────────┤
│                                     │
│  Username: [admin            ]      │
│  Password: [                 ]      │
│                                     │
│  [        Login        ]            │
│                                     │
│  ─────────── or ───────────         │
│                                     │
│  [  Sign in with Google  ]          │
│  [  Sign in with Microsoft ]        │
│                                     │
└─────────────────────────────────────┘

(OAuth buttons only shown if configured)
```

### First Login Flow (Password Required)

```
Login as "admin" (no password)
        ↓
Check: MustChangePassword = true?
        ↓ yes
Redirect to Set Password page
        ↓
┌─────────────────────────────────────┐
│      Set Your Password              │
├─────────────────────────────────────┤
│                                     │
│  Welcome! Please set a password     │
│  to secure your account.            │
│                                     │
│  New Password:     [             ]  │
│  Confirm Password: [             ]  │
│                                     │
│  [        Save        ]             │
│                                     │
└─────────────────────────────────────┘
        ↓
MustChangePassword = false
        ↓
Redirect to home page
```

### Logout

```
User menu (top right)
├── Display Name
├── ─────────────
├── My Account
└── Logout
```

---

## 19. Page Access Control

### Protected vs Public Pages

```csharp
// Public (Anonymous can access)
@page "/dashboard"
@attribute [Authorize(Roles = "Anonymous")]

// Requires login
@page "/settings"
@attribute [Authorize(Roles = "User")]

// Admin only
@page "/users"
@attribute [Authorize(Roles = "Admin")]

@page "/roles"
@attribute [Authorize(Roles = "Admin")]
```

### App.razor Authorization Setup

```razor
<CascadingAuthenticationState>
    <Router AppAssembly="@typeof(App).Assembly">
        <Found Context="routeData">
            <AuthorizeRouteView RouteData="@routeData"
                                DefaultLayout="@typeof(MainLayout)">
                <NotAuthorized>
                    @if (context.User.Identity?.IsAuthenticated != true)
                    {
                        <RedirectToLogin />
                    }
                    else
                    {
                        <AccessDenied />
                    }
                </NotAuthorized>
            </AuthorizeRouteView>
        </Found>
        <NotFound>
            <PageNotFound />
        </NotFound>
    </Router>
</CascadingAuthenticationState>
```

---

## 20. Navigation Filtering

### Menu Items

Hide menu items the user can't access:

```razor
<AuthorizeView Roles="Admin">
    <NavLink href="/users">Users</NavLink>
    <NavLink href="/roles">Roles</NavLink>
</AuthorizeView>

<AuthorizeView Roles="User">
    <NavLink href="/settings">Settings</NavLink>
</AuthorizeView>

<NavLink href="/dashboard">Dashboard</NavLink>  <!-- Always visible -->
```

### Subject Browser Filtering

Filter subjects based on State+Read access:

```csharp
public class AuthorizedSubjectBrowser
{
    public IEnumerable<IInterceptorSubject> GetVisibleSubjects(
        IInterceptorSubject root,
        ClaimsPrincipal user)
    {
        var userRoles = GetExpandedRoles(user);

        return registry.GetAllSubjects()
            .Where(subject =>
            {
                // Check if user can read any state property
                var readRoles = resolver.ResolveSubjectRoles(subject, Kind.State, AuthorizationAction.Read);
                return readRoles.Any(role => userRoles.Contains(role));
            });
    }
}
```

### Property/Method Filtering in UI

```razor
@foreach (var property in subject.Properties)
{
    var kind = GetPropertyKind(property);  // State or Configuration
    var roles = resolver.ResolvePropertyRoles(property, kind, AuthorizationAction.Read);
    if (UserHasAnyRole(roles))
    {
        <PropertyEditor Property="@property" />
    }
}

@foreach (var method in subject.Methods)
{
    var kind = GetMethodKind(method);  // Query or Operation
    var roles = resolver.ResolveMethodRoles(subject, method.Name, kind);
    if (UserHasAnyRole(roles))
    {
        <MethodButton Method="@method" />
    }
}
```

---

## 21. Access Denied Handling

### Subject Operations

When a user attempts an unauthorized operation:

```csharp
[RunsFirst]
public class AuthorizationInterceptor : IWriteInterceptor
{
    public void WriteProperty<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        WriteInterceptionDelegate<TProperty> next)
    {
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IAuthorizationResolver>();
        if (resolver == null) { next(ref context); return; }

        var kind = GetPropertyKind(context.Property);  // State or Configuration
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Write);
        var user = AuthorizationContext.CurrentUser;

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - interceptor throws as defense in depth
            throw new UnauthorizedAccessException(
                $"Access denied: {kind}+Write on {context.Property.PropertyName}");
        }

        next(ref context);
    }
}
```

### UI Feedback

```
┌─────────────────────────────────────────────┐
│ ⚠️ Access Denied                        ✕  │
│                                             │
│ You don't have permission to modify         │
│ "SecuritySystem.ArmCode"                    │
│                                             │
│ Required role: Admin                        │
└─────────────────────────────────────────────┘
```

### Read-Only Mode

Properties user can read but not write show as disabled:

```razor
<MudTextField Value="@value"
              Disabled="@(!CanWrite)"
              ReadOnly="@(!CanWrite)"
              HelperText="@(CanWrite ? null : "Read-only")" />
```

---

## 22. My Account Page

```
┌─────────────────────────────────────────────────────────────┐
│ My Account                                                  │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ Profile                                                     │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Display Name: [John Doe                    ]            │ │
│ │ Email:        john@example.com (from Google)            │ │
│ │ Member since: December 25, 2025                         │ │
│ └─────────────────────────────────────────────────────────┘ │
│                                                             │
│ Your Roles                                                  │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ • Admin (includes: HomeOwner, User, Guest, Anonymous)   │ │
│ └─────────────────────────────────────────────────────────┘ │
│                                                             │
│ Security                                                    │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Password:  ●●●●●●●●  [Change Password]                  │ │
│ │ Last login: December 25, 2025 at 10:30 AM               │ │
│ └─────────────────────────────────────────────────────────┘ │
│                                                             │
│ Linked Accounts                                             │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Google    john@gmail.com              [Unlink]          │ │
│ │ Microsoft (not linked)                [Link]            │ │
│ └─────────────────────────────────────────────────────────┘ │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Change Password Dialog

```
┌─────────────────────────────────────┐
│ Change Password                     │
├─────────────────────────────────────┤
│                                     │
│ Current Password: [             ]   │
│ New Password:     [             ]   │
│ Confirm Password: [             ]   │
│                                     │
│ Password requirements:              │
│ • At least 8 characters             │
│ • At least one number               │
│                                     │
│        [Cancel]  [Save]             │
└─────────────────────────────────────┘
```

---

## 23. Remember Me & Session

### Login with Remember Me

```
┌─────────────────────────────────────┐
│           HomeBlaze                 │
├─────────────────────────────────────┤
│                                     │
│  Username: [admin            ]      │
│  Password: [                 ]      │
│                                     │
│  ☐ Remember me                      │
│                                     │
│  [        Login        ]            │
│                                     │
└─────────────────────────────────────┘
```

### Session Configuration

```json
{
  "Authentication": {
    "Session": {
      "DefaultTimeout": "01:00:00",
      "RememberMeTimeout": "30.00:00:00"
    }
  }
}
```

### Implementation

```csharp
services.ConfigureApplicationCookie(options =>
{
    options.ExpireTimeSpan = TimeSpan.FromHours(1);
    options.SlidingExpiration = true;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
});

// On login
await signInManager.PasswordSignInAsync(
    user,
    password,
    isPersistent: rememberMe,  // Uses RememberMeTimeout if true
    lockoutOnFailure: true
);
```

---

## 24. Password Requirements

### Configuration

```json
{
  "Authentication": {
    "Password": {
      "RequiredLength": 6
    }
  }
}
```

### Implementation

```csharp
services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    // Simple password policy - 6 chars minimum, no complexity requirements
    options.Password.RequiredLength = 6;
    options.Password.RequireDigit = false;
    options.Password.RequireLowercase = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;

    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
    options.Lockout.MaxFailedAccessAttempts = 5;
});
```

### UI Validation

```razor
<MudTextField @bind-Value="password"
              InputType="InputType.Password"
              Validation="@(new PasswordValidator())" />

@if (password.Length > 0)
{
    <MudText Typo="Typo.caption">
        @(password.Length >= 8 ? "✓" : "✗") At least 8 characters
        @(password.Any(char.IsDigit) ? "✓" : "✗") At least one number
    </MudText>
}
```

---

## 25. User Context in Interceptors

### Problem

Interceptors (`IReadInterceptor`, `IWriteInterceptor`) need access to the current user, but they're not HTTP-aware and may be singletons.

### Solution: Scoped User Context Service

```csharp
public interface IAuthorizationUserContext
{
    ClaimsPrincipal? CurrentUser { get; }
    string[] GetExpandedRoles();
    bool HasAnyRole(params string[] roles);
    bool IsAuthenticated { get; }
}

public class BlazorAuthorizationUserContext : IAuthorizationUserContext
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IRoleExpander _roleExpander;

    public ClaimsPrincipal? CurrentUser
    {
        get
        {
            var authState = _authStateProvider
                .GetAuthenticationStateAsync()
                .GetAwaiter()
                .GetResult();
            return authState.User;
        }
    }

    public string[] GetExpandedRoles()
    {
        var roles = CurrentUser?.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToArray() ?? [];

        return _roleExpander.ExpandRoles(roles);
    }

    public bool HasAnyRole(params string[] roles)
    {
        var userRoles = GetExpandedRoles();
        return roles.Any(r => userRoles.Contains(r));
    }

    public bool IsAuthenticated => CurrentUser?.Identity?.IsAuthenticated ?? false;
}
```

### Access in Interceptors via DI

```csharp
[RunsFirst] // Ensure auth check happens before any other interceptor
public class AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor
{
    public void WriteProperty<TProperty>(
        ref PropertyWriteContext<TProperty> context,
        WriteInterceptionDelegate<TProperty> next)
    {
        // Resolve services from DI via IServiceProvider
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IAuthorizationResolver>();
        if (resolver == null) { next(ref context); return; } // Graceful degradation

        var kind = GetPropertyKind(context.Property);  // State or Configuration
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Write);
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - this is defense in depth
            throw new UnauthorizedAccessException(
                $"{kind}+Write denied for '{context.Property.PropertyName}'");
        }

        next(ref context);
    }

    public TProperty ReadProperty<TProperty>(
        ref PropertyReadContext context,
        ReadInterceptionDelegate<TProperty> next)
    {
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IAuthorizationResolver>();
        if (resolver == null) return next(ref context); // Graceful degradation

        var kind = GetPropertyKind(context.Property);  // State or Configuration
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Read);
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - this is defense in depth
            throw new UnauthorizedAccessException(
                $"{kind}+Read denied for '{context.Property.PropertyName}'");
        }

        return next(ref context);
    }

    private Kind GetPropertyKind(PropertyReference property)
    {
        return property.Metadata.HasAttribute<ConfigurationAttribute>()
            ? Kind.Configuration
            : Kind.State;
    }

    private static bool HasAnyRole(ClaimsPrincipal user, string[] requiredRoles)
    {
        var userRoles = AuthorizationContext.CurrentRoles; // Cached HashSet
        return requiredRoles.Any(r => userRoles.Contains(r));
    }
}
```

### Async Considerations

For async methods, use async version:

```csharp
public async Task<ClaimsPrincipal?> GetCurrentUserAsync()
{
    var authState = await _authStateProvider.GetAuthenticationStateAsync();
    return authState.User;
}
```

### Blazor Server Circuit Handler (CRITICAL)

**Problem**: Middleware alone is NOT sufficient for Blazor Server. Simple `OnCircuitOpenedAsync` doesn't cover all scenarios (lazy rendering, virtualization, auth state changes mid-circuit).

**Solution**: Use `CreateInboundActivityHandler` (.NET 8+) which wraps **EVERY** inbound circuit activity.

**Reference**: [ASP.NET Core Blazor dependency injection](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-9.0)

```csharp
public class CircuitServicesAccessor
{
    private static readonly AsyncLocal<IServiceProvider?> _services = new();
    public IServiceProvider? Services
    {
        get => _services.Value;
        set => _services.Value = value;
    }
}

public class AuthorizationCircuitHandler : CircuitHandler
{
    private readonly IServiceProvider _services;
    private readonly CircuitServicesAccessor _servicesAccessor;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IRoleExpander _roleExpander;
    private readonly AuthorizationOptions _options;

    // Constructor with DI...

    /// <summary>
    /// Wraps EVERY inbound circuit activity (UI events, JS interop, renders).
    /// This ensures AuthorizationContext is always set during any operation.
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            _servicesAccessor.Services = _services;
            await UpdateAuthorizationContextAsync();
            await next(context);  // Activity runs here with AsyncLocal set
        };
    }

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        _servicesAccessor.Services = _services;
        await UpdateAuthorizationContextAsync();
        await base.OnCircuitOpenedAsync(circuit, ct);
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        _servicesAccessor.Services = null;
        return base.OnCircuitClosedAsync(circuit, ct);
    }

    private async Task UpdateAuthorizationContextAsync()
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        var roles = user.Identity?.IsAuthenticated == true
            ? user.FindAll(ClaimTypes.Role).Select(c => c.Value)
            : [_options.UnauthenticatedRole]; // "Anonymous"

        var expanded = _roleExpander.ExpandRoles(roles);
        AuthorizationContext.SetUser(user, expanded);
    }
}

// Registration
services.AddScoped<CircuitServicesAccessor>();
services.AddScoped<CircuitHandler, AuthorizationCircuitHandler>();
```

**Why This Works**:

```
User clicks button
       │
       ▼
CreateInboundActivityHandler fires FIRST
       │
       ├── servicesAccessor.Services = _services
       ├── await UpdateAuthorizationContextAsync()
       │   └── AuthorizationContext.SetUser() ← AsyncLocal SET
       │
       ▼
await next(context) ← Entire activity runs here
       │
       ├── Event handler executes
       ├── StateHasChanged() called
       ├── Lazy @if blocks render
       ├── Virtualized items render
       ├── property.Value accessed
       └── Interceptor reads AuthorizationContext ← STILL SET ✓
```

**Covered Scenarios**:
- ✅ Button clicks, form submissions
- ✅ Lazy `@if` rendering
- ✅ Virtualized list items
- ✅ JS interop callbacks
- ✅ Login/logout (UpdateAuthorizationContextAsync refreshes)
- ⚠️ Prerendering (needs middleware - see below)

---

## 26. Authorization Data Persistence

### Where Data is Stored

Subject authorization data uses the existing extension data system with namespaced keys:

```csharp
// Stored in Subject.Data dictionary with "HomeBlaze.Authorization:{Kind}:{Action}" format
subject.Data[(null, "HomeBlaze.Authorization:State:Read")] = new[] { "Guest" };                // Subject-level (null = subject)
subject.Data[("MacAddress", "HomeBlaze.Authorization:Configuration:Read")] = new[] { "Admin" }; // Property-level
```

The `HomeBlaze.Authorization:` prefix follows the Namotion convention (e.g., `Namotion.Interceptor.WriteTimestamp`) to avoid conflicts with other extensions.

### Extensible Serialization via ISubjectDataProvider

The `ConfigurableSubjectSerializer` only serializes `[Configuration]` properties by default. To persist `Subject.Data` extension data, we introduce an extensibility point:

#### Interface (in HomeBlaze.Abstractions)

```csharp
/// <summary>
/// Provides additional data to serialize/deserialize with subjects.
/// Implementations are called by ConfigurableSubjectSerializer.
/// </summary>
public interface ISubjectDataProvider
{
    /// <summary>
    /// Returns additional properties to serialize with the subject.
    /// Keys are plain names (e.g., "permissions") - serializer adds "$" prefix.
    /// Return null if no data to add.
    /// </summary>
    Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject);

    /// <summary>
    /// Receives additional "$" properties from deserialized JSON.
    /// Keys have "$" prefix stripped (e.g., "$authorization" → "authorization").
    /// Provider picks out the ones it handles and ignores the rest.
    /// </summary>
    void SetAdditionalProperties(IInterceptorSubject subject, Dictionary<string, JsonElement> properties);
}
```

#### Why "$" Prefix?

The `$` prefix creates a clear namespace separation in JSON:

| Property Type | Example | Source |
|--------------|---------|--------|
| Type discriminator | `$type` | Built-in (System.Text.Json) |
| Configuration | `name`, `isArmed` | `[Configuration]` attributes |
| Extension data | `$authorization` | `ISubjectDataProvider` |

This prevents conflicts - a subject can have both a `[Configuration] string Authorization` property and `$authorization` extension data.

#### Serializer Integration

```csharp
public class ConfigurableSubjectSerializer
{
    private readonly IEnumerable<ISubjectDataProvider> _dataProviders;

    public ConfigurableSubjectSerializer(
        TypeProvider typeProvider,
        IServiceProvider serviceProvider,
        IEnumerable<ISubjectDataProvider> dataProviders)
    {
        _dataProviders = dataProviders;
        // ... existing setup ...
    }

    public string Serialize(IInterceptorSubject subject)
    {
        // ... write $type and [Configuration] properties ...

        // Collect and write provider data with "$" prefix
        foreach (var provider in _dataProviders)
        {
            var props = provider.GetAdditionalProperties(subject);
            if (props != null)
            {
                foreach (var (key, value) in props)
                {
                    writer.WritePropertyName($"${key}");
                    JsonSerializer.Serialize(writer, value, _options);
                }
            }
        }
    }

    public IConfigurableSubject? Deserialize(string json)
    {
        // ... create subject, populate [Configuration] properties ...

        // Collect "$" properties (strip prefix) and pass to providers
        var additionalProperties = new Dictionary<string, JsonElement>();
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name.StartsWith('$') && prop.Name != "$type")
            {
                var cleanKey = prop.Name[1..]; // Remove "$"
                additionalProperties[cleanKey] = prop.Value.Clone();
            }
        }

        foreach (var provider in _dataProviders)
        {
            provider.SetAdditionalProperties(subject, additionalProperties);
        }

        return subject;
    }
}
```

#### Authorization Provider Implementation

**Descendant Storage**: Only `IConfigurableSubject` subjects are serialized to JSON. Permission overrides on non-configurable descendants must be persisted with the nearest configurable ancestor.

- **Runtime**: Overrides stored in each subject's own `Data` dictionary (works normally)
- **Serialize**: Walk descendants, collect permissions from non-`IConfigurableSubject` children, store with path
- **Deserialize**: Use path resolver to navigate to target subject, restore to correct `Data` dictionary

**Path format** (uses existing path resolver patterns):
- `""` = subject-level on this configurable subject (default for all properties)
- `PropertyName` = property on this subject
- `Child/PropertyName` = property on child subject
- `Children[2]/PropertyName` = property on indexed child

All non-empty paths resolve to properties via `ISubjectPathResolver`.

```csharp
public class AuthorizationDataProvider : ISubjectDataProvider
{
    private const string DataKeyPrefix = "HomeBlaze.Authorization:";
    private const string PropertyKey = "authorization";

    public Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject)
    {
        // 1. Collect authorization overrides from this subject
        // 2. Walk descendants, collect from non-IConfigurableSubject children
        // 3. Return flat format if no descendant overrides, hierarchical otherwise
        // (See implementation plan Task 8b for full code)
    }

    public void SetAdditionalProperties(
        IInterceptorSubject subject,
        Dictionary<string, JsonElement> properties)
    {
        // 1. Try flat format first (backward compat / simple case)
        // 2. Parse hierarchical format with $descendant: paths
        // 3. Resolve each path and apply permissions to correct subject
        // (See implementation plan Task 8b for full code)
    }
}
```

### Authorization Override Schema

```csharp
public class AuthorizationOverride
{
    public bool Inherit { get; set; }  // true = extend, false = replace
    public string[] Roles { get; set; } = [];
}
```

- `inherit: false` → Replace: Only these roles can access (ignores parent/defaults)
- `inherit: true` → Extend: These roles can also access, in addition to inherited roles

### JSON Output

Simple case (overrides on this subject only):
```json
{
  "$type": "HomeBlaze.Samples.SecuritySystem",
  "name": "Security",
  "isArmed": false,
  "$authorization": {
    "": {
      "State:Read": { "inherit": false, "roles": ["SecurityGuard"] },
      "Operation:Invoke": { "inherit": false, "roles": ["Admin"] }
    },
    "ArmCode": {
      "Configuration:Read": { "inherit": false, "roles": ["Admin"] },
      "Configuration:Write": { "inherit": false, "roles": ["Admin"] }
    }
  }
}
```

With descendant overrides (Room has non-configurable Thermostat child):
```json
{
  "$type": "HomeBlaze.Samples.Room",
  "name": "Living Room",
  "$authorization": {
    "": {
      "State:Read": { "inherit": true, "roles": ["Guest"] }
    },
    "Thermostat/TargetTemperature": {
      "State:Write": { "inherit": false, "roles": ["Admin"] }
    },
    "Thermostat/Sensor/Calibration": {
      "Configuration:Write": { "inherit": false, "roles": ["Admin"] }
    }
  }
}
```

### Registration

```csharp
// In HomeBlaze.Services (serializer picks up all providers)
services.AddSingleton<ConfigurableSubjectSerializer>();

// In HomeBlaze.Authorization
services.AddSingleton<ISubjectDataProvider, AuthorizationDataProvider>();
```

### Extension Methods for Authorization

```csharp
public static class SubjectAuthorizationExtensions
{
    private const string KeyPrefix = "HomeBlaze.Authorization:";

    public static void SetPermissionRoles(
        this IInterceptorSubject subject,
        string permission,
        string[] roles)
    {
        subject.Data[("", $"{KeyPrefix}{permission}")] = roles;
    }

    public static string[]? GetPermissionRoles(
        this IInterceptorSubject subject,
        string permission)
    {
        return subject.Data.TryGetValue(("", $"{KeyPrefix}{permission}"), out var roles)
            ? roles as string[]
            : null;
    }

    public static void SetPropertyPermissionRoles(
        this PropertyReference property,
        string permission,
        string[] roles)
    {
        property.SetPropertyData($"{KeyPrefix}{permission}", roles);
    }

    public static string[]? GetPropertyPermissionRoles(
        this PropertyReference property,
        string permission)
    {
        return property.TryGetPropertyData($"{KeyPrefix}{permission}", out var roles)
            ? roles as string[]
            : null;
    }
}
```

### Parent Traversal for Inheritance

Uses existing `GetParents()` API from `Namotion.Interceptor.Tracking.Parent`:

```csharp
using Namotion.Interceptor.Tracking.Parent;

public class AuthorizationResolver : IAuthorizationResolver
{
    private const string KeyPrefix = "HomeBlaze.Authorization:";

    public string[] ResolvePropertyRoles(PropertyReference property, string permission)
    {
        // 1. Check property extension data
        if (property.TryGetPropertyData($"{KeyPrefix}{permission}", out var propRoles))
            return (string[])propRoles!;

        // 2. Check property attribute
        var attrRoles = GetPropertyAttributeRoles(property, permission);
        if (attrRoles != null)
            return attrRoles;

        // 3. Check subject extension data
        var subject = property.Subject;
        if (subject.Data.TryGetValue(("", $"{KeyPrefix}{permission}"), out var subjectRoles))
            return (string[])subjectRoles!;

        // 4. Check subject attribute
        var subjectAttrRoles = GetSubjectAttributeRoles(subject, permission);
        if (subjectAttrRoles != null)
            return subjectAttrRoles;

        // 5. Traverse parents (OR combine)
        var inheritedRoles = TraverseParents(subject, permission);
        if (inheritedRoles.Length > 0)
            return inheritedRoles;

        // 6. Global defaults
        return _options.GetDefaultPermissionRoles(permission);
    }

    private string[] TraverseParents(IInterceptorSubject subject, string permission)
    {
        var allRoles = new HashSet<string>();

        foreach (var parentRef in subject.GetParents())
        {
            var parentSubject = parentRef.Property.Subject;

            // Check if parent has roles defined (ext data OR attribute)
            if (parentSubject.Data.TryGetValue(("", $"{KeyPrefix}{permission}"), out var roles))
            {
                // Parent has roles - add them and stop this branch
                foreach (var role in (string[])roles!)
                    allRoles.Add(role);
            }
            else if (GetSubjectAttributeRoles(parentSubject, permission) is { } attrRoles)
            {
                // Parent has attribute roles - add them and stop this branch
                foreach (var role in attrRoles)
                    allRoles.Add(role);
            }
            else
            {
                // No roles on parent - continue up this branch
                var ancestorRoles = TraverseParents(parentSubject, permission);
                foreach (var role in ancestorRoles)
                    allRoles.Add(role);
            }
        }

        return allRoles.ToArray();
    }
}
```

---

## 27. Real-Time Role Changes

### Problem

When admin changes a user's roles, what happens to their active session?

### Solution: Revalidating Auth State

```csharp
public class RevalidatingAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authState,
        CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.GetUserAsync(authState.User);
        if (user == null)
            return false; // User deleted

        // Check if roles changed
        var currentRoles = await userManager.GetRolesAsync(user);
        var claimRoles = authState.User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .ToHashSet();

        if (!currentRoles.ToHashSet().SetEquals(claimRoles))
        {
            // Roles changed - force re-auth
            return false;
        }

        return true;
    }
}
```

### Registration

```csharp
services.AddScoped<AuthenticationStateProvider,
    RevalidatingAuthenticationStateProvider>();
```

### Behavior

| Event | Delay | Effect |
|-------|-------|--------|
| Role added | Up to 1 min | User gets new permissions |
| Role removed | Up to 1 min | User loses permissions |
| User deleted | Up to 1 min | Logged out |

### Optional: Immediate Invalidation

For instant effect, use SignalR to notify connected clients:

```csharp
public class RoleChangeNotifier
{
    private readonly IHubContext<AuthHub> _hubContext;

    public async Task NotifyRoleChange(string userId)
    {
        await _hubContext.Clients
            .User(userId)
            .SendAsync("RolesChanged");
    }
}

// Client-side
hubConnection.On("RolesChanged", async () =>
{
    // Force re-authentication
    await authStateProvider.RefreshAsync();
});
```

---

## 28. Blazor Server Auth State Lifecycle

### How It Works

```
Browser                    Server
   │                         │
   │──── Initial HTTP ──────>│ Cookie auth, get user claims
   │                         │
   │<─── HTML + SignalR ─────│
   │                         │
   │──── SignalR Connect ───>│ Circuit created
   │                         │ AuthenticationState captured
   │                         │
   │<─── Interactive UI ─────│ Uses cached auth state
   │                         │
   ├──── [Time passes] ──────┤ Cookie may expire
   │                         │
   │ Revalidation timer ─────│ Check if still valid
   │                         │
```

### Cookie vs Circuit Lifetime

| Scenario | Cookie | Circuit | Result |
|----------|--------|---------|--------|
| Normal use | Valid | Active | Works |
| Cookie expires | Expired | Active | Revalidation fails → logout |
| Tab closed | Valid | Closed | New circuit on return |
| Remember me | 30 days | N/A | Long-lived auth |

### Circuit Auth Handler

```csharp
public class CircuitAuthHandler : CircuitHandler
{
    private readonly AuthenticationStateProvider _authProvider;

    public override async Task OnCircuitOpenedAsync(Circuit circuit,
        CancellationToken cancellationToken)
    {
        // Validate auth on circuit creation
        var authState = await _authProvider.GetAuthenticationStateAsync();
        if (!authState.User.Identity?.IsAuthenticated ?? true)
        {
            // Handle unauthenticated circuit
        }
    }

    public override Task OnCircuitClosedAsync(Circuit circuit,
        CancellationToken cancellationToken)
    {
        // Cleanup if needed
        return Task.CompletedTask;
    }
}
```

---

## 29. Integration Points

### Where Authorization Hooks In

```
┌─────────────────────────────────────────────────────────────┐
│                    HomeBlaze Architecture                   │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│  ┌─────────────┐    ┌─────────────┐    ┌─────────────┐     │
│  │   Blazor    │───>│    Auth     │───>│  Subject    │     │
│  │     UI      │    │  Filtering  │    │  Browser    │     │
│  └─────────────┘    └─────────────┘    └─────────────┘     │
│         │                                     │             │
│         │ Read/Write                          │             │
│         ▼                                     ▼             │
│  ┌─────────────────────────────────────────────────┐       │
│  │           InterceptorSubjectContext             │       │
│  │  ┌───────────────────────────────────────────┐  │       │
│  │  │         Authorization Interceptor         │  │  ← HERE│
│  │  │  (IReadInterceptor, IWriteInterceptor)    │  │       │
│  │  └───────────────────────────────────────────┘  │       │
│  │  ┌───────────────────────────────────────────┐  │       │
│  │  │           Other Interceptors              │  │       │
│  │  │   (Tracking, Validation, Lifecycle)       │  │       │
│  │  └───────────────────────────────────────────┘  │       │
│  └─────────────────────────────────────────────────┘       │
│         │                                                   │
│         ▼                                                   │
│  ┌─────────────────────────────────────────────────┐       │
│  │              Subject Properties                 │       │
│  └─────────────────────────────────────────────────┘       │
│                                                             │
└─────────────────────────────────────────────────────────────┘
```

### Registration

```csharp
// In Program.cs - DI registration
services.AddHomeBlazeAuthorization(options =>
{
    options.UnauthenticatedRole = DefaultRoles.Anonymous;

    // Defaults by Kind and Action
    options.DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
    {
        // State defaults (runtime property values)
        [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Guest],
        [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Operator],

        // Configuration defaults (persisted settings)
        [(Kind.Configuration, AuthorizationAction.Read)] = [DefaultRoles.User],
        [(Kind.Configuration, AuthorizationAction.Write)] = [DefaultRoles.Supervisor],

        // Query defaults (read-only methods)
        [(Kind.Query, AuthorizationAction.Invoke)] = [DefaultRoles.User],

        // Operation defaults (state-changing methods)
        [(Kind.Operation, AuthorizationAction.Invoke)] = [DefaultRoles.Operator]
    };
});

// Service registration (inside AddHomeBlazeAuthorization)
public static IServiceCollection AddHomeBlazeAuthorization(
    this IServiceCollection services,
    Action<AuthorizationOptions> configure)
{
    var options = new AuthorizationOptions();
    configure(options);

    services.AddSingleton(options);
    services.AddSingleton<IRoleExpander, RoleExpander>();
    services.AddSingleton<IExternalRoleMapper, ExternalRoleMapper>();
    services.AddScoped<IAuthorizationResolver, AuthorizationResolver>(); // Per-request caching

    return services;
}

// Extension method (in HomeBlaze.Authorization)
public static class AuthorizationContextExtensions
{
    public static IInterceptorSubjectContext WithAuthorization(this IInterceptorSubjectContext context)
    {
        return context.WithService(() => new AuthorizationInterceptor());
    }
}

// Update SubjectContextFactory.cs
public static class SubjectContextFactory
{
    public static IInterceptorSubjectContext Create(IServiceProvider serviceProvider)
    {
        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithReadPropertyRecorder()
            .WithRegistry()
            .WithParents()
            .WithLifecycle()
            .WithService<ILifecycleHandler>(
                () => new MethodPropertyInitializer(),
                handler => handler is MethodPropertyInitializer)
            .WithAuthorization() // Extension method
            .WithDataAnnotationValidation()
            .WithHostedServices(services);

        // CRITICAL: Register IServiceProvider for DI resolution in interceptors
        context.AddService(serviceProvider);

        return context;
    }
}
```

### Method Authorization

Methods with `[Operation]` or `[Query]` attributes are registered via `MethodPropertyInitializer`. Authorization uses same pattern:

```csharp
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    public async Task<object?> InvokeAsync(
        IInterceptorSubject subject,
        string methodName,
        object?[] parameters)
    {
        var resolver = subject.Context.GetService<IAuthorizationResolver>();
        var userContext = subject.Context.GetService<IAuthorizationUserContext>();

        // Get method Kind from [Query] or [Operation] attribute
        var kind = GetMethodKind(subject, methodName); // Kind.Query or Kind.Operation
        var requiredRoles = resolver.ResolveMethodRoles(subject, methodName, kind);

        if (!userContext.HasAnyRole(requiredRoles))
        {
            throw new UnauthorizedAccessException(
                $"Cannot invoke {methodName}");
        }

        // Invoke actual method
        return await _innerInvoker.InvokeAsync(subject, methodName, parameters);
    }

    private Kind GetMethodKind(IInterceptorSubject subject, string methodName)
    {
        var method = subject.GetType().GetMethod(methodName);
        if (method?.GetCustomAttribute<QueryAttribute>() != null)
            return Kind.Query;
        return Kind.Operation; // Default to Operation for methods
    }
}
```

### UI Layer Integration

```csharp
// Inject into Blazor components
@inject IAuthorizationResolver AuthResolver
@inject IAuthorizationUserContext UserContext

// Check access before rendering - Kind determined from [State]/[Configuration] attribute
@if (UserContext.HasAnyRole(AuthResolver.ResolvePropertyRoles(property, GetPropertyKind(property), AuthorizationAction.Read)))
{
    <PropertyEditor Property="@property"
                    ReadOnly="@(!CanWrite(property))" />
}

@code {
    private Kind GetPropertyKind(PropertyReference property)
    {
        // Determined from [State] or [Configuration] attribute on the property
        var hasConfiguration = property.Metadata.PropertyInfo?
            .GetCustomAttribute<ConfigurationAttribute>() != null;
        return hasConfiguration ? Kind.Configuration : Kind.State;
    }

    private bool CanWrite(PropertyReference property)
    {
        var kind = GetPropertyKind(property);
        return UserContext.HasAnyRole(
            AuthResolver.ResolvePropertyRoles(property, kind, AuthorizationAction.Write));
    }
}
```

---

## Summary

### Complete Feature Set

| Category | Features |
|----------|----------|
| **Authentication** | Local accounts, OAuth (configurable), Remember me |
| **Users** | CRUD, role assignment, default admin seed |
| **Roles** | Composable, external mapping, anonymous role |
| **Permissions** | Read, Write, Invoke + custom keys |
| **Authorization** | Subject/property/method level, multi-parent inheritance (OR) |
| **UI - Admin** | User management, Role configuration |
| **UI - Subjects** | Authorization panel, property/method overrides |
| **UI - User** | Login, Set password, My Account, Change password |
| **Filtering** | Nav menu, Subject browser, Properties, Methods |
| **Feedback** | Access denied toasts, Read-only indicators |
| **Security** | Password requirements, Session timeout, Account lockout |

---

## 30. Implementation Decisions

| Question | Decision |
|----------|----------|
| Project location | Standalone `HomeBlaze.Authorization` project |
| Test framework | Both unit and integration tests |
| OAuth packages | Only `Microsoft.AspNetCore.Authentication.OpenIdConnect` |
| Scoped service access | Via `IServiceProvider` → `IHttpContextAccessor`, no user = no restrictions |
| Unauthorized read | Filter at UI + throw exception if read bypasses UI |
| Role storage | Database (runtime changes via admin UI) |
| Connection string | `ConnectionStrings:IdentityDatabase` |

### Terminology

| Old Term | New Term |
|----------|----------|
| Permission string | Kind + Action tuple |
| `[Permission("Read")]` | `[SubjectPropertyAuthorize(AuthorizationAction.Read, ...)]` |
| `PermissionAttribute` | `SubjectAuthorize`, `SubjectPropertyAuthorize`, `SubjectMethodAuthorize` |
| Permission level | Kind enum (State, Configuration, Query, Operation) |

### IdentitySeeding

Seeds both the admin user AND default roles on first startup:

```csharp
public class IdentitySeeding : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed default roles with composition (stored in database)
        var defaultRoles = new Dictionary<string, string[]>
        {
            ["Anonymous"] = [],
            ["Guest"] = ["Anonymous"],
            ["User"] = ["Guest"],
            ["Operator"] = ["User"],
            ["Supervisor"] = ["Operator"],
            ["Admin"] = ["Supervisor"]
        };

        foreach (var (roleName, includes) in defaultRoles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
                // Store composition in Role entity
            }
        }

        // Seed admin user if no users exist
        if (!await userManager.Users.AnyAsync())
        {
            var admin = new ApplicationUser
            {
                UserName = "admin",
                Email = "admin@localhost",
                MustChangePassword = true
            };
            await userManager.CreateAsync(admin);
            await userManager.AddToRoleAsync(admin, "Admin");
        }
    }
}
```

### Method Authorization

Inject permission check into existing `ISubjectMethodInvoker`:

```csharp
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    private readonly ISubjectMethodInvoker _inner;
    private readonly IAuthorizationResolver _resolver;
    private readonly IAuthorizationUserContext _userContext;

    public async Task<object?> InvokeAsync(
        IInterceptorSubject subject,
        string methodName,
        object?[] parameters)
    {
        // Check permission
        var requiredRoles = _resolver.ResolveMethodRoles(subject, methodName);

        if (_userContext.CurrentUser != null &&
            !_userContext.HasAnyRole(requiredRoles))
        {
            throw new UnauthorizedAccessException(
                $"Cannot invoke {methodName}");
        }

        return await _inner.InvokeAsync(subject, methodName, parameters);
    }
}
```

**Defense in depth:**
1. UI hides unauthorized methods (primary)
2. Service throws if invoked anyway (safety net)

### AuthorizationDiscoveryService

Provides the fixed set of Kind+Action combinations for authorization configuration:

```csharp
public interface IAuthorizationDiscoveryService
{
    IReadOnlyList<(Kind Kind, Action Action)> GetAllKindActionCombinations();
    string FormatStorageKey(Kind kind, AuthorizationAction action);
}

public class AuthorizationDiscoveryService : IAuthorizationDiscoveryService
{
    /// <summary>
    /// Returns all valid Kind+Action combinations for permission configuration.
    /// These are fixed and don't need discovery - they're defined by the enums.
    /// </summary>
    public IReadOnlyList<(Kind Kind, Action Action)> GetAllKindActionCombinations()
    {
        return new List<(Kind, AuthorizationAction)>
        {
            // State (runtime property values)
            (Kind.State, AuthorizationAction.Read),
            (Kind.State, AuthorizationAction.Write),

            // Configuration (persisted settings)
            (Kind.Configuration, AuthorizationAction.Read),
            (Kind.Configuration, AuthorizationAction.Write),

            // Query (read-only methods)
            (Kind.Query, AuthorizationAction.Invoke),

            // Operation (state-changing methods)
            (Kind.Operation, AuthorizationAction.Invoke)
        };
    }

    /// <summary>
    /// Formats Kind+Action as storage key suffix.
    /// </summary>
    public string FormatStorageKey(Kind kind, AuthorizationAction action) => $"{kind}:{action}";
}
```

### External Role Mapping Entity

```csharp
public class ExternalRoleMapping
{
    public int Id { get; set; }
    public string Provider { get; set; } = "";      // "Google", "Microsoft", etc.
    public string ExternalRole { get; set; } = "";  // Claim value from provider
    public string InternalRole { get; set; } = "";  // Maps to internal role name
}
```

**DbContext:**
```csharp
public class AuthorizationDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<ExternalRoleMapping> ExternalRoleMappings { get; set; }
    public DbSet<RoleComposition> RoleCompositions { get; set; }
}

public class RoleComposition
{
    public int Id { get; set; }
    public string RoleName { get; set; } = "";
    public string IncludesRole { get; set; } = "";  // One row per included role
}
```

### User Context Resolution

**Note**: The primary user context is provided via `AuthorizationContext` (AsyncLocal), which is set by `AuthorizationCircuitHandler` for Blazor and `AuthorizationContextMiddleware` for HTTP requests. This avoids sync-over-async issues.

```csharp
/// <summary>
/// Primary pattern: Use AuthorizationContext static class (set by circuit handler/middleware)
/// </summary>
public static class AuthorizationContext
{
    private static readonly AsyncLocal<ClaimsPrincipal?> _currentUser = new();
    private static readonly AsyncLocal<HashSet<string>?> _expandedRoles = new();

    public static ClaimsPrincipal? CurrentUser => _currentUser.Value;
    public static HashSet<string> ExpandedRoles => _expandedRoles.Value ?? [];

    public static void SetUser(ClaimsPrincipal? user, IEnumerable<string> expandedRoles)
    {
        _currentUser.Value = user;
        _expandedRoles.Value = expandedRoles.ToHashSet();
    }

    public static bool HasAnyRole(IEnumerable<string> requiredRoles)
    {
        // No user context = allow all (background tasks)
        if (CurrentUser == null)
            return true;

        return requiredRoles.Any(ExpandedRoles.Contains);
    }
}
```

### Authorization Behavior by Context

| Context | User Available | Authorization |
|---------|---------------|---------------|
| HTTP request | ✓ | Enforced |
| Blazor circuit | ✓ | Enforced |
| Background service | ✗ | No restrictions |
| Startup/seeding | ✗ | No restrictions |

### Unauthorized Read Behavior

Defense in depth:
1. **UI filters** unauthorized properties (primary)
2. **Throw exception** if read happens anyway (safety net)

---

## Next Steps

1. See implementation plan: `docs/plans/2025-12-25-homeblaze-authorization-implementation.md`
2. Implementation
