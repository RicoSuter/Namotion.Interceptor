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

Roles can include other roles (OR combination):

```json
{
  "Authorization": {
    "Roles": {
      "Guest": [],
      "User": ["Guest"],
      "SecurityGuard": ["Guest"],
      "HomeOwner": ["User"],
      "Admin": ["HomeOwner", "SecurityGuard"]
    }
  }
}
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

## 3. Permissions

### Built-in Permissions

| Permission | Purpose |
|------------|---------|
| `Read` | Read property value |
| `Write` | Write property value |
| `Invoke` | Invoke method |

### Custom Permissions

Any string key for domain-specific access:
- `Config` - configuration access
- `AdminRead` - admin-only state
- `Diagnostics` - diagnostic information

---

## 4. Property/Method Tagging

### Default Behavior

Properties use `Read`/`Write`, methods use `Invoke`:

```csharp
[InterceptorSubject]
public partial class Light
{
    public partial bool IsOn { get; set; }  // Read, Write
    public void Toggle() { }                 // Invoke
}
```

### Explicit Permission

```csharp
[InterceptorSubject]
public partial class Light
{
    [Permission("Read")]
    public partial bool IsOn { get; set; }

    [Permission("Config")]  // custom permission
    public partial string MacAddress { get; set; }

    [Permission("Invoke")]
    public void Toggle() { }

    [Permission("AdminInvoke")]
    public void FactoryReset() { }
}
```

---

## 5. Subject Role Mappings

Subjects map permissions to roles.

### Runtime (Extension Data)

```csharp
subject.SetPermissionRoles("Read", ["Guest", "User", "Admin"]);
subject.SetPermissionRoles("Write", ["User", "Admin"]);
subject.SetPermissionRoles("Invoke", ["User", "Admin"]);
subject.SetPermissionRoles("Config", ["Admin"]);
```

### Compile-time (Attributes)

```csharp
[DefaultRoles("Read", "Guest", "User")]
[DefaultRoles("Write", "User")]
[DefaultRoles("Invoke", "User")]
[DefaultRoles("Config", "Admin")]
public partial class Light { }
```

---

## 6. Property-Level Override

Properties can override subject-level mappings:

```csharp
var property = subject.GetProperty("MacAddress");
property.SetPropertyData("Read:Roles", ["Admin"]);
```

---

## 7. Resolution Priority

For a property/method with permission `X`:

```
1. Property ExtData["X:Roles"]?  → use it, STOP
        ↓ no
2. Property Attribute[X]?        → use it, STOP
        ↓ no
3. Subject ExtData["X:Roles"]?   → use it, STOP
        ↓ no
4. Subject Attribute[X]?         → use it, STOP
        ↓ no
5. Traverse Parent Subjects      → OR combine
        ↓ none found
6. Global Defaults               → use it
```

---

## 8. Multi-Parent Inheritance

Subjects can have multiple parents (graph structure).

### Example

```
          Home
         /    \
   LivingRoom  Kitchen
   (Read→[Guest])    (Read→[Chef])
         \    /
          Light ← TWO parents
```

### Traversal Rules

1. Traverse ALL parent branches
2. Stop at first ancestor with roles defined (ExtData OR Attribute)
3. That ancestor contributes its roles
4. **OR combine** across all branches
5. Defining roles = override point (stops further traversal up that branch)

### Result

- LivingRoom: `Read→[Guest]` → stops, contributes [Guest]
- Kitchen: `Read→[Chef]` → stops, contributes [Chef]
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
ResolvePropertyRoles(property, permission):
  1. property.ExtData[permission + ":Roles"]? → return it
  2. property.Attribute[permission]?          → return it
  3. subject.ExtData[permission + ":Roles"]?  → return it
  4. subject.Attribute[permission]?           → return it
  5. TraverseParents(subject, permission)     → OR combine
  6. GlobalDefaults[permission]               → return it

TraverseParents(subject, permission):
  roles = []
  for each parent in subject.Parents:
    if parent.ExtData[permission] OR parent.Attribute[permission]:
      roles = roles OR (parent.ExtData ?? parent.Attribute)
      // STOP this branch
    else:
      roles = roles OR TraverseParents(parent, permission)
  return roles
```

---

## 11. Data Storage

Using Namotion.Interceptor extension data:

```csharp
// Subject-level
subject.Data[("", "Read:Roles")] = new[] { "Guest" };
subject.Data[("", "Write:Roles")] = new[] { "User" };

// Property-level override
subject.Data[("MacAddress", "Read:Roles")] = new[] { "Admin" };
```

---

## 12. Full Example

```
Subject Graph:
──────────────
Home
├── ExtData: Read→[Guest], Write→[User], Invoke→[User], Config→[Admin]
│
├── LivingRoom
│   └── (no override, inherits from Home)
│   │
│   └── Light1
│       ├── IsOn [Permission("Read")]       → inherits [Guest]
│       ├── Brightness [Permission("Read")] → inherits [Guest]
│       └── Toggle() [Permission("Invoke")] → inherits [User]
│
└── SecuritySystem
    ├── ExtData: Read→[SecurityGuard], Invoke→[Admin]  ← OVERRIDE
    │
    └── Camera
        ├── IsRecording [Permission("Read")]   → inherits [SecurityGuard]
        ├── MacAddress [Permission("Config")]  → traverses to Home → [Admin]
        └── FactoryReset() [Permission("Invoke")] → inherits [Admin]
```

**Access Check: `Camera.IsRecording` with user roles ["Guest"]:**
1. Property ExtData? No
2. Property Attribute? Permission="Read"
3. Camera ExtData["Read:Roles"]? No
4. Camera Attribute? No
5. Traverse → SecuritySystem has Read→[SecurityGuard]
6. Required: [SecurityGuard], User has: [Guest] → **DENIED**

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
│   │   └── RoleDefinition.cs
│   ├── Roles/
│   │   ├── RoleExpander.cs
│   │   └── ExternalRoleMapper.cs
│   ├── Resolution/
│   │   ├── IPermissionResolver.cs
│   │   ├── PermissionResolver.cs
│   │   └── ResolvedRoles.cs
│   ├── Attributes/
│   │   ├── PermissionAttribute.cs
│   │   └── DefaultRolesAttribute.cs
│   ├── Extensions/
│   │   └── SubjectAuthorizationExtensions.cs
│   └── ServiceCollectionExtensions.cs
│
├── HomeBlaze.Authorization.Blazor/
│   ├── Components/
│   │   ├── AuthorizationPanel.razor
│   │   ├── PropertyRolesEditor.razor
│   │   └── RoleMappingDialog.razor
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

### Subject Authorization Panel

Inline panel shown when viewing/editing a subject:

```
┌─────────────────────────────────────────────────────────────┐
│ SecuritySystem                                   [Auth ▼]   │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│ Subject Permissions                            [+ Add]    │
│ ┌──────────────┬─────────────────────┬───────────┬───────┐ │
│ │ Level        │ Roles               │ Source    │Actions│ │
│ ├──────────────┼─────────────────────┼───────────┼───────┤ │
│ │ Read         │ SecurityGuard       │ Override  │ ✏️ ↩️ │ │
│ │ Write        │ Admin               │ Override  │ ✏️ ↩️ │ │
│ │ Invoke       │ Admin               │ Override  │ ✏️ ↩️ │ │
│ │ Config       │ Admin               │ Inherited │       │ │
│ └──────────────┴─────────────────────┴───────────┴───────┘ │
│                                                             │
│ Property Overrides                                          │
│ ┌──────────────┬────────┬─────────────────┬────────┬─────┐ │
│ │ Property     │ Level  │ Roles           │ Source │     │ │
│ ├──────────────┼────────┼─────────────────┼────────┼─────┤ │
│ │ IsRecording  │ Read   │ SecurityGuard   │ Subject│     │ │
│ │ MacAddress   │ Read   │ Admin ✏️        │Override│ ↩️  │ │
│ │ LastMotion   │ Read   │ SecurityGuard   │ Subject│     │ │
│ └──────────────┴────────┴─────────────────┴────────┴─────┘ │
│                                                             │
│ Method Overrides                                            │
│ ┌──────────────┬────────┬─────────────────┬────────┬─────┐ │
│ │ Method       │ Level  │ Roles           │ Source │     │ │
│ ├──────────────┼────────┼─────────────────┼────────┼─────┤ │
│ │ StartRecord  │ Invoke │ Admin           │ Subject│     │ │
│ │ FactoryReset │ Invoke │ SuperAdmin ✏️   │Override│ ↩️  │ │
│ └──────────────┴────────┴─────────────────┴────────┴─────┘ │
└─────────────────────────────────────────────────────────────┘

Legend:
  ✏️  = Edit (opens dialog)
  ↩️  = Clear override (revert to inherited/attribute)
  Source: Override | Subject | Inherited | Attribute | Global
```

### Edit Role Mapping Dialog

```
┌─────────────────────────────────────┐
│ Edit: Read Roles                    │
├─────────────────────────────────────┤
│ Permission: Read                  │
│                                     │
│ Current Source: Inherited (Home)    │
│ Inherited Value: [Guest]            │
│                                     │
│ ○ Use inherited [Guest]             │
│ ● Override with:                    │
│   ☑ Admin                           │
│   ☐ HomeOwner                       │
│   ☐ User                            │
│   ☑ SecurityGuard                   │
│   ☐ Guest                           │
│                                     │
│        [Cancel]  [Save]             │
└─────────────────────────────────────┘
```

### Inheritance Visualization

Optional: Show inheritance chain on hover/expand:

```
┌─────────────────────────────────────────────────────────────┐
│ Read Roles: [SecurityGuard]                                 │
│                                                             │
│ Inheritance Chain:                                          │
│ ┌─────────────────────────────────────────────────────────┐ │
│ │ Camera (this)                                           │ │
│ │   └── No override                                       │ │
│ │       ↓                                                 │ │
│ │ SecuritySystem (parent)                                 │ │
│ │   └── Read: [SecurityGuard] ← RESOLVED                  │ │
│ │       (stops here)                                      │ │
│ │                                                         │ │
│ │ Home (grandparent)                                      │ │
│ │   └── Read: [Guest] (not reached)                       │ │
│ └─────────────────────────────────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### UI Component Structure

```
HomeBlaze.Authorization.Blazor/
├── Pages/
│   ├── UserManagement.razor
│   └── RoleConfiguration.razor
│
├── Components/
│   ├── SubjectAuthorizationPanel.razor
│   ├── PermissionTable.razor
│   ├── PropertyOverrideTable.razor
│   ├── MethodOverrideTable.razor
│   ├── RoleMappingDialog.razor
│   ├── InheritanceChainView.razor
│   └── RoleSelector.razor
│
└── Services/
    └── AuthorizationUIService.cs
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
      "Admin": ["User"]
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
// Public read access
subject.SetPermissionRoles("Read", ["Anonymous"]);

// Requires login
subject.SetPermissionRoles("Write", ["User"]);
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

Filter subjects based on Read access:

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
                var readRoles = resolver.ResolvePermission(subject, "Read");
                return readRoles.Any(role => userRoles.Contains(role));
            });
    }
}
```

### Property/Method Filtering in UI

```razor
@foreach (var property in subject.Properties)
{
    var roles = resolver.ResolvePropertyRoles(property, "Read");
    if (UserHasAnyRole(roles))
    {
        <PropertyEditor Property="@property" />
    }
}

@foreach (var method in subject.Methods)
{
    var roles = resolver.ResolveMethodRoles(method, "Invoke");
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
        var resolver = serviceProvider?.GetService<IPermissionResolver>();
        if (resolver == null) { next(ref context); return; }

        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, "Write");
        var user = AuthorizationContext.CurrentUser;

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - interceptor throws as defense in depth
            throw new UnauthorizedAccessException(
                $"Access denied: cannot modify {context.Property.PropertyName}");
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
      "RequiredLength": 8,
      "RequireDigit": true,
      "RequireLowercase": false,
      "RequireUppercase": false,
      "RequireNonAlphanumeric": false
    }
  }
}
```

### Implementation

```csharp
services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = true;
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
        var resolver = serviceProvider?.GetService<IPermissionResolver>();
        if (resolver == null) { next(ref context); return; } // Graceful degradation

        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, "Write");
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - this is defense in depth
            throw new UnauthorizedAccessException(
                $"Write access denied for property '{context.Property.PropertyName}'");
        }

        next(ref context);
    }

    public TProperty ReadProperty<TProperty>(
        ref PropertyReadContext context,
        ReadInterceptionDelegate<TProperty> next)
    {
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IPermissionResolver>();
        if (resolver == null) return next(ref context); // Graceful degradation

        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, "Read");
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
        {
            // UI should filter - this is defense in depth
            throw new UnauthorizedAccessException(
                $"Read access denied for property '{context.Property.PropertyName}'");
        }

        return next(ref context);
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
// Stored in Subject.Data dictionary with "HomeBlaze.Authorization:" prefix
subject.Data[("", "HomeBlaze.Authorization:Read")] = new[] { "Guest" };           // Subject-level
subject.Data[("MacAddress", "HomeBlaze.Authorization:Read")] = new[] { "Admin" }; // Property-level
```

The `HomeBlaze.Authorization:` prefix follows the Namotion convention (e.g., `Namotion.Interceptor.WriteTimestamp`) to avoid conflicts with other extensions.

### Extensible Serialization via ISubjectDataProvider

The `ConfigurableSubjectSerializer` only serializes `[Configuration]` properties by default. To persist `Subject.Data` extension data, we introduce an extensibility point:

#### Interface (in HomeBlaze.Services)

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
    /// Keys have "$" prefix stripped (e.g., "$permissions" → "permissions").
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
| Extension data | `$permissions` | `ISubjectDataProvider` |

This prevents conflicts - a subject can have both a `[Configuration] string Permissions` property and `$permissions` extension data.

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

```csharp
public class PermissionsDataProvider : ISubjectDataProvider
{
    private const string DataKeyPrefix = "HomeBlaze.Authorization:";
    private const string PropertyKey = "permissions";

    public Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject)
    {
        // Structure: { "propertyName": { "permission": ["role1", "role2"] } }
        var permissions = new Dictionary<string, Dictionary<string, string[]>>();

        foreach (var kvp in subject.Data)
        {
            if (!kvp.Key.key.StartsWith(DataKeyPrefix) || kvp.Value is not string[] roles)
                continue;

            var permission = kvp.Key.key[DataKeyPrefix.Length..]; // "Read", "Write", etc.
            var propertyName = kvp.Key.property ?? "";

            if (!permissions.TryGetValue(propertyName, out var permDict))
            {
                permDict = [];
                permissions[propertyName] = permDict;
            }
            permDict[permission] = roles;
        }

        return permissions.Count > 0
            ? new Dictionary<string, object> { [PropertyKey] = permissions }
            : null;
    }

    public void SetAdditionalProperties(
        IInterceptorSubject subject,
        Dictionary<string, JsonElement> properties)
    {
        if (!properties.TryGetValue(PropertyKey, out var element))
            return;

        var permissions = element.Deserialize<Dictionary<string, Dictionary<string, string[]>>>();
        if (permissions == null)
            return;

        foreach (var (propertyName, permissionMap) in permissions)
        {
            foreach (var (permission, roles) in permissionMap)
            {
                subject.Data[(propertyName, $"{DataKeyPrefix}{permission}")] = roles;
            }
        }
    }
}
```

### JSON Output

```json
{
  "$type": "HomeBlaze.Samples.SecuritySystem",
  "name": "Security",
  "isArmed": false,
  "$permissions": {
    "": {
      "Read": ["SecurityGuard"],
      "Write": ["Admin"],
      "Invoke": ["Admin"]
    },
    "ArmCode": {
      "Read": ["Admin"],
      "Write": ["Admin"]
    }
  }
}
```

### Registration

```csharp
// In HomeBlaze.Services (serializer picks up all providers)
services.AddSingleton<ConfigurableSubjectSerializer>();

// In HomeBlaze.Authorization
services.AddSingleton<ISubjectDataProvider, PermissionsDataProvider>();
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

public class PermissionResolver : IPermissionResolver
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
        return _options.GetDefaultRoles(permission);
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
    options.UnauthenticatedRole = "Anonymous";
    options.DefaultRoles = new Dictionary<string, string[]>
    {
        ["Read"] = ["Anonymous"],
        ["Write"] = ["User"],
        ["Invoke"] = ["User"]
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
    services.AddScoped<IPermissionResolver, PermissionResolver>(); // Per-request caching

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
        var resolver = subject.Context.GetService<IPermissionResolver>();
        var userContext = subject.Context.GetService<IAuthorizationUserContext>();

        var requiredRoles = resolver.ResolveMethodRoles(subject, methodName);

        if (!userContext.HasAnyRole(requiredRoles))
        {
            throw new UnauthorizedAccessException(
                $"Cannot invoke {methodName}");
        }

        // Invoke actual method
        return await _innerInvoker.InvokeAsync(subject, methodName, parameters);
    }
}
```

### UI Layer Integration

```csharp
// Inject into Blazor components
@inject IPermissionResolver AuthResolver
@inject IAuthorizationUserContext UserContext

// Check access before rendering
@if (UserContext.HasAnyRole(AuthResolver.ResolvePropertyRoles(property, "Read")))
{
    <PropertyEditor Property="@property"
                    ReadOnly="@(!CanWrite(property))" />
}

@code {
    private bool CanWrite(PropertyReference property) =>
        UserContext.HasAnyRole(AuthResolver.ResolvePropertyRoles(property, "Write"));
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
| Permission | Permission |
| `[Permission("Read")]` | `[Permission("Read")]` |
| `PermissionAttribute` | `PermissionAttribute` |

### IdentitySeeding

Seeds both the admin user AND default roles on first startup:

```csharp
public class IdentitySeeding : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Seed default roles with composition
        var defaultRoles = new Dictionary<string, string[]>
        {
            ["Anonymous"] = [],
            ["Guest"] = ["Anonymous"],
            ["User"] = ["Guest"],
            ["Admin"] = ["User"]
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
    private readonly IPermissionResolver _resolver;
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

### PermissionDiscoveryService

Discovers permissions from attributes across all subject types:

```csharp
public interface IPermissionDiscoveryService
{
    IReadOnlyList<string> GetAllPermissions();
}

public class PermissionDiscoveryService : IPermissionDiscoveryService
{
    private readonly Lazy<IReadOnlyList<string>> _permissions;

    public PermissionDiscoveryService(ITypeProvider typeProvider)
    {
        _permissions = new Lazy<IReadOnlyList<string>>(() =>
        {
            var permissions = new HashSet<string>
            {
                "Read", "Write", "Invoke" // Built-in
            };

            // Scan all subject types for [Permission] attributes
            foreach (var type in typeProvider.GetSubjectTypes())
            {
                foreach (var prop in type.GetProperties())
                {
                    var attr = prop.GetCustomAttribute<PermissionAttribute>();
                    if (attr != null)
                        permissions.Add(attr.Name);
                }

                foreach (var method in type.GetMethods())
                {
                    var attr = method.GetCustomAttribute<PermissionAttribute>();
                    if (attr != null)
                        permissions.Add(attr.Name);
                }
            }

            return permissions.OrderBy(p => p).ToList();
        });
    }

    public IReadOnlyList<string> GetAllPermissions() => _permissions.Value;
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

```csharp
public class BlazorAuthorizationUserContext : IAuthorizationUserContext
{
    private readonly IServiceProvider _serviceProvider;

    public ClaimsPrincipal? CurrentUser
    {
        get
        {
            // Try HTTP context first
            var httpContextAccessor = _serviceProvider.GetService<IHttpContextAccessor>();
            if (httpContextAccessor?.HttpContext?.User is { } user)
                return user;

            // Try Blazor auth state
            var authStateProvider = _serviceProvider.GetService<AuthenticationStateProvider>();
            if (authStateProvider != null)
            {
                var authState = authStateProvider.GetAuthenticationStateAsync()
                    .GetAwaiter().GetResult();
                return authState.User;
            }

            return null; // No user context = no restrictions
        }
    }

    public bool HasAnyRole(params string[] roles)
    {
        if (CurrentUser == null)
            return true; // No user context = allow all (background tasks)

        var userRoles = GetExpandedRoles();
        return roles.Any(r => userRoles.Contains(r));
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
