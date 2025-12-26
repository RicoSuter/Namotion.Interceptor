# HomeBlaze Authorization System Documentation

This document provides a comprehensive overview of all authorization and authentication components, their locations, and responsibilities.

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Project Structure](#project-structure)
3. [Component Reference](#component-reference)
4. [Data Flow](#data-flow)
5. [Configuration](#configuration)
6. [Troubleshooting](#troubleshooting)

---

## Architecture Overview

The authorization system follows a layered approach:

```
┌─────────────────────────────────────────────────────────────────────────┐
│                           UI LAYER (Filtering)                          │
│  Components use <AuthorizeView> and permission checks to hide/disable   │
│  unauthorized content. This is for UX only - NOT security.              │
├─────────────────────────────────────────────────────────────────────────┤
│                        CONTEXT LAYER (Flow)                             │
│  AuthorizationCircuitHandler + Middleware populate AsyncLocal with      │
│  current user for every operation.                                      │
├─────────────────────────────────────────────────────────────────────────┤
│                      INTERCEPTOR LAYER (Enforcement)                    │
│  AuthorizationInterceptor checks permissions on every property          │
│  read/write. Throws UnauthorizedAccessException if denied.              │
├─────────────────────────────────────────────────────────────────────────┤
│                       SERVICE LAYER (Resolution)                        │
│  IPermissionResolver determines required roles for each property.       │
│  IRoleExpander expands role hierarchy (Admin → User → Guest).           │
├─────────────────────────────────────────────────────────────────────────┤
│                        DATA LAYER (Storage)                             │
│  IdentityDbContext stores users, roles. Subject ExtData stores          │
│  per-subject/property permission overrides.                             │
└─────────────────────────────────────────────────────────────────────────┘
```

---

## Project Structure

```
src/HomeBlaze/
├── HomeBlaze.Authorization/              # Core authorization library
│   ├── Attributes/
│   │   ├── PermissionAttribute.cs        # [Permission("Read")] on properties
│   │   └── DefaultPermissionsAttribute.cs # [DefaultPermissions(...)] on classes
│   │
│   ├── Context/
│   │   ├── AuthorizationContext.cs       # Static AsyncLocal for current user
│   │   ├── CircuitServicesAccessor.cs    # AsyncLocal for scoped IServiceProvider
│   │   ├── AuthorizationCircuitHandler.cs # Blazor circuit handler
│   │   └── AuthorizationContextMiddleware.cs # HTTP middleware
│   │
│   ├── Interceptors/
│   │   └── AuthorizationInterceptor.cs   # [RunsFirst] property interceptor
│   │
│   ├── Services/
│   │   ├── IPermissionResolver.cs        # Interface for permission resolution
│   │   ├── PermissionResolver.cs         # Implementation with caching
│   │   ├── IRoleExpander.cs              # Interface for role hierarchy
│   │   └── RoleExpander.cs               # Implementation with DB-backed config
│   │
│   ├── Options/
│   │   └── AuthorizationOptions.cs       # Configuration options
│   │
│   └── Extensions/
│       ├── AuthorizationContextExtensions.cs  # .WithAuthorization() for context
│       └── ServiceCollectionExtensions.cs     # .AddHomeBlazeAuthorization()
│
├── HomeBlaze.Identity/                   # ASP.NET Core Identity integration
│   ├── Data/
│   │   ├── IdentityDbContext.cs          # EF Core context for users/roles
│   │   ├── ApplicationUser.cs            # Custom user entity
│   │   └── RoleComposition.cs            # Role hierarchy entity
│   │
│   ├── Services/
│   │   ├── IdentitySeeding.cs            # Creates default admin user
│   │   └── HomeBlazeAuthenticationStateProvider.cs # Revalidating auth state
│   │
│   └── Extensions/
│       └── IdentityServiceExtensions.cs  # .AddHomeBlazeIdentity()
│
├── HomeBlaze.Components/                 # Blazor UI components
│   ├── Authorization/
│   │   ├── AuthorizationContextProvider.razor # Wraps app, provides auth context
│   │   ├── SubjectAuthorizationPanel.razor    # UI for per-subject permissions
│   │   └── PermissionBadge.razor              # Shows permission status
│   │
│   ├── Account/
│   │   ├── Login.razor                   # Login page
│   │   ├── Logout.razor                  # Logout handler
│   │   ├── SetPassword.razor             # Initial password setup
│   │   └── MyAccount.razor               # User profile page
│   │
│   └── Admin/
│       ├── UserManagement.razor          # CRUD for users
│       └── RoleConfiguration.razor       # Role hierarchy editor
│
├── HomeBlaze.Services/                   # Core services
│   ├── SubjectContextFactory.cs          # Creates context with auth interceptor
│   └── SubjectPathResolver.cs            # Uses permissions for filtering
│
└── HomeBlaze/                            # Main application
    ├── Program.cs                        # DI registration, middleware
    └── App.razor                         # Root component with auth wrapper
```

---

## Component Reference

### 1. AuthorizationContext

**Location**: `HomeBlaze.Authorization/Context/AuthorizationContext.cs`

**Purpose**: Static class providing access to current user via AsyncLocal.

```csharp
public static class AuthorizationContext
{
    public static ClaimsPrincipal? CurrentUser { get; }
    public static HashSet<string> ExpandedRoles { get; }

    public static void SetUser(ClaimsPrincipal? user, IEnumerable<string> roles);
    public static bool HasAnyRole(IEnumerable<string> requiredRoles);
}
```

**Used By**:
- `AuthorizationInterceptor` - reads current user during property access
- UI components - check permissions for filtering

---

### 2. AuthorizationCircuitHandler

**Location**: `HomeBlaze.Authorization/Context/AuthorizationCircuitHandler.cs`

**Purpose**: Sets `AuthorizationContext` for every Blazor circuit activity.

**Key Method**:
```csharp
public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
    Func<CircuitInboundActivityContext, Task> next)
{
    return async context =>
    {
        _servicesAccessor.Services = _services;
        await UpdateAuthorizationContextAsync();  // Sets AsyncLocal
        await next(context);                       // Activity runs with context set
    };
}
```

**Handles**:
- Button clicks, form submissions
- Lazy rendering (@if blocks)
- Virtualized list items
- JS interop callbacks
- Login/logout state changes

---

### 3. AuthorizationContextMiddleware

**Location**: `HomeBlaze.Authorization/Context/AuthorizationContextMiddleware.cs`

**Purpose**: Sets `AuthorizationContext` for HTTP requests (API calls, SSR/prerendering).

```csharp
public async Task InvokeAsync(HttpContext context, IRoleExpander expander)
{
    var user = context.User;
    var roles = user.Identity?.IsAuthenticated == true
        ? user.FindAll(ClaimTypes.Role).Select(c => c.Value)
        : ["Anonymous"];

    AuthorizationContext.SetUser(user, expander.ExpandRoles(roles));

    try { await _next(context); }
    finally { AuthorizationContext.SetUser(null, []); }
}
```

**Handles**:
- Web API requests
- SSR/prerendering (before circuit exists)

---

### 4. AuthorizationInterceptor

**Location**: `HomeBlaze.Authorization/Interceptors/AuthorizationInterceptor.cs`

**Purpose**: Enforces authorization on property read/write operations.

```csharp
[RunsFirst]  // Runs before all other interceptors
public class AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor
{
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ...)
    {
        var resolver = GetPermissionResolver(context);
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, "Read");

        if (!AuthorizationContext.HasAnyRole(requiredRoles))
            throw new UnauthorizedAccessException(...);

        return next(ref context);
    }
}
```

**Key Points**:
- Uses `[RunsFirst]` to execute before other interceptors
- Resolves `IPermissionResolver` from DI via `IServiceProvider`
- Throws `UnauthorizedAccessException` on denied access
- UI should filter first; interceptor is defense-in-depth

---

### 5. PermissionResolver

**Location**: `HomeBlaze.Authorization/Services/PermissionResolver.cs`

**Purpose**: Determines which roles are required for a given property/permission.

```csharp
public class PermissionResolver : IPermissionResolver
{
    public string[] ResolvePropertyRoles(PropertyReference property, string permission)
    {
        // 1. Check property-level override in ExtData
        if (TryGetPropertyRoles(property, permission, out var roles))
            return roles;

        // 2. Check subject-level override in ExtData
        if (TryGetSubjectRoles(property.Subject, permission, out roles))
            return roles;

        // 3. Walk parent hierarchy (with inheritance)
        if (TryGetInheritedRoles(property.Subject, permission, out roles))
            return roles;

        // 4. Check [Permission] attribute on property
        if (TryGetAttributeRoles(property, permission, out roles))
            return roles;

        // 5. Check [DefaultPermissions] attribute on class
        if (TryGetDefaultRoles(property.Subject, permission, out roles))
            return roles;

        // 6. Fall back to global defaults
        return _options.DefaultRoles[permission];
    }
}
```

**Resolution Order**:
1. Property ExtData override
2. Subject ExtData override
3. Parent subject inheritance
4. `[Permission]` attribute on property
5. `[DefaultPermissions]` attribute on class
6. Global defaults from `AuthorizationOptions`

---

### 6. RoleExpander

**Location**: `HomeBlaze.Authorization/Services/RoleExpander.cs`

**Purpose**: Expands role hierarchy (e.g., Admin → User → Guest → Anonymous).

```csharp
public class RoleExpander : IRoleExpander
{
    // Cached hierarchy from database
    private Dictionary<string, HashSet<string>> _hierarchy;

    public IReadOnlySet<string> ExpandRoles(IEnumerable<string> roles)
    {
        var expanded = new HashSet<string>();
        foreach (var role in roles)
        {
            expanded.Add(role);
            if (_hierarchy.TryGetValue(role, out var included))
                expanded.UnionWith(included);
        }
        return expanded;
    }

    public async Task ReloadAsync()
    {
        // Reload from database
        var compositions = await _dbContext.RoleCompositions.ToListAsync();
        _hierarchy = BuildHierarchy(compositions);
    }
}
```

**Configuration**:
- Stored in `RoleComposition` table in database
- Editable via Admin UI (`RoleConfiguration.razor`)
- Default: Admin includes User includes Guest includes Anonymous

---

### 7. HomeBlazeAuthenticationStateProvider

**Location**: `HomeBlaze.Identity/Services/HomeBlazeAuthenticationStateProvider.cs`

**Purpose**: Revalidates user authentication every 30 minutes.

```csharp
public class HomeBlazeAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authState, CancellationToken ct)
    {
        // Check if user still exists and security stamp matches
        var user = await _userManager.GetUserAsync(authState.User);
        if (user == null) return false;

        var currentStamp = await _userManager.GetSecurityStampAsync(user);
        var claimStamp = authState.User.FindFirstValue("AspNet.Identity.SecurityStamp");

        return currentStamp == claimStamp;
    }
}
```

**Detects**:
- User deleted
- Password changed
- Roles changed
- Session invalidated

---

### 8. Permission Attributes

**Location**: `HomeBlaze.Authorization/Attributes/`

```csharp
// On properties - define required permission
[Permission("Config")]  // Only users with "Config" permission can access
public partial string SensitiveData { get; set; }

// On classes - define defaults for all properties
[DefaultPermissions("Read", "Anonymous")]   // Anyone can read
[DefaultPermissions("Write", "User")]       // Users can write
[DefaultPermissions("Invoke", "User")]      // Users can invoke methods
public partial class Device { }
```

---

### 9. Subject Extension Data

**Location**: Stored in `Subject.Data` dictionary, serialized with subject

```csharp
// Subject-level permission override
subject.SetPermissionRoles("", "Read", ["Admin"]);
subject.SetPermissionRoles("", "Write", ["Admin"]);

// Property-level permission override
subject.SetPermissionRoles("MacAddress", "Read", ["Admin"]);

// Check current permissions
var roles = subject.GetPermissionRoles("MacAddress", "Read");
```

**Storage Format**:
```json
{
  "Type": "SecuritySystem",
  "Data": {
    ":Read:Roles": ["SecurityGuard"],
    ":Write:Roles": ["Admin"],
    "ArmCode:Read:Roles": ["Admin"]
  }
}
```

---

### 10. UI Components

**Location**: `HomeBlaze.Components/Authorization/`

#### AuthorizationContextProvider.razor
Wraps the entire app to ensure `CascadingAuthenticationState` is available:
```razor
<CascadingAuthenticationState>
    @ChildContent
</CascadingAuthenticationState>
```

#### SubjectAuthorizationPanel.razor
UI for editing per-subject/property permissions:
```razor
<SubjectAuthorizationPanel Subject="@subject" />
```

#### Permission Checking in Components
```razor
@inject IPermissionResolver PermissionResolver

@if (CanRead("MacAddress"))
{
    <PropertyEditor Property="MacAddress" ReadOnly="@(!CanWrite("MacAddress"))" />
}

@code {
    private bool CanRead(string property) =>
        AuthorizationContext.HasAnyRole(
            PermissionResolver.ResolvePropertyRoles(_subject, property, "Read"));
}
```

---

## Data Flow

### HTTP API Request

```
1. Request arrives
   │
   ▼
2. AuthorizationContextMiddleware.InvokeAsync()
   ├── Read HttpContext.User
   ├── Expand roles via IRoleExpander
   └── Set AuthorizationContext (AsyncLocal)
   │
   ▼
3. Controller/Endpoint executes
   │
   ▼
4. Property accessed (e.g., person.Name)
   │
   ▼
5. AuthorizationInterceptor.ReadProperty()
   ├── Read AuthorizationContext.CurrentUser
   ├── Resolve required roles via IPermissionResolver
   ├── Check if user has required roles
   └── Throw or allow
   │
   ▼
6. Response returned
```

### Blazor Circuit Activity

```
1. User clicks button
   │
   ▼
2. CreateInboundActivityHandler() wraps activity
   ├── Set CircuitServicesAccessor.Services
   └── Call UpdateAuthorizationContextAsync()
       ├── Get auth state from AuthenticationStateProvider
       ├── Expand roles via IRoleExpander
       └── Set AuthorizationContext (AsyncLocal)
   │
   ▼
3. await next(context) - activity executes
   │
   ▼
4. Event handler runs
   ├── May trigger StateHasChanged()
   └── May access properties
   │
   ▼
5. Component re-renders (still in same activity context)
   │
   ▼
6. Lazy @if blocks render
   │
   ▼
7. Property accessed
   │
   ▼
8. AuthorizationInterceptor checks (AsyncLocal still set) ✓
```

---

## Configuration

### Program.cs

```csharp
// Add Identity (users, roles, database)
builder.Services.AddHomeBlazeIdentity(options =>
{
    // Uses ASP.NET Identity defaults
});

// Add Authorization (interceptors, resolvers, circuit handler)
builder.Services.AddHomeBlazeAuthorization(options =>
{
    options.UnauthenticatedRole = "Anonymous";
    options.DefaultRoles = new Dictionary<string, string[]>
    {
        ["Read"] = ["Anonymous"],
        ["Write"] = ["User"],
        ["Invoke"] = ["User"]
    };
});

// Configure authentication
builder.Services.AddAuthentication()
    .AddCookie()
    .AddGoogle(options => { ... });  // Optional OAuth

var app = builder.Build();

// Middleware (order matters!)
app.UseAuthentication();
app.UseMiddleware<AuthorizationContextMiddleware>();  // Before routing
app.UseAuthorization();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
```

### App.razor

```razor
<!DOCTYPE html>
<html>
<head>...</head>
<body>
    <AuthorizationContextProvider>
        <Router AppAssembly="@typeof(App).Assembly">
            <Found Context="routeData">
                <AuthorizeRouteView RouteData="@routeData"
                                    DefaultLayout="@typeof(MainLayout)">
                    <NotAuthorized>
                        <RedirectToLogin />
                    </NotAuthorized>
                </AuthorizeRouteView>
            </Found>
        </Router>
    </AuthorizationContextProvider>
</body>
</html>
```

### SubjectContextFactory.cs

```csharp
public static IInterceptorSubjectContext Create(IServiceProvider serviceProvider)
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithAuthorization()  // Adds AuthorizationInterceptor
        .WithDataAnnotationValidation();

    // CRITICAL: Register IServiceProvider for DI resolution in interceptors
    context.AddService(serviceProvider);

    return context;
}
```

---

## Troubleshooting

### User Not Authorized (but should be)

1. **Check AuthorizationContext is set**:
   ```csharp
   var user = AuthorizationContext.CurrentUser;
   var roles = AuthorizationContext.ExpandedRoles;
   Console.WriteLine($"User: {user?.Identity?.Name}, Roles: {string.Join(", ", roles)}");
   ```

2. **Check role expansion**:
   ```csharp
   var expander = serviceProvider.GetRequiredService<IRoleExpander>();
   var expanded = expander.ExpandRoles(["User"]);
   // Should include: User, Guest, Anonymous
   ```

3. **Check permission resolution**:
   ```csharp
   var resolver = serviceProvider.GetRequiredService<IPermissionResolver>();
   var roles = resolver.ResolvePropertyRoles(property, "Read");
   Console.WriteLine($"Required roles: {string.Join(", ", roles)}");
   ```

### AsyncLocal Not Set in Interceptor

1. **Verify middleware is registered** (for HTTP):
   ```csharp
   app.UseMiddleware<AuthorizationContextMiddleware>();
   ```

2. **Verify circuit handler is registered** (for Blazor):
   ```csharp
   services.AddScoped<CircuitHandler, AuthorizationCircuitHandler>();
   ```

3. **Check IServiceProvider is on context**:
   ```csharp
   var sp = context.Property.Subject.Context.TryGetService<IServiceProvider>();
   if (sp == null) throw new InvalidOperationException("IServiceProvider not registered");
   ```

### Permission Changes Not Taking Effect

1. **Clear permission cache**:
   ```csharp
   var resolver = serviceProvider.GetRequiredService<IPermissionResolver>();
   resolver.ClearCache();
   ```

2. **Reload role hierarchy**:
   ```csharp
   var expander = serviceProvider.GetRequiredService<IRoleExpander>();
   await expander.ReloadAsync();
   ```

3. **Force auth state revalidation**:
   - User logs out and back in
   - Wait for 30-minute revalidation interval
   - Or reduce `RevalidationInterval` for testing

### Debugging Tips

1. **Enable detailed logging**:
   ```json
   {
     "Logging": {
       "LogLevel": {
         "HomeBlaze.Authorization": "Debug"
       }
     }
   }
   ```

2. **Add interceptor logging**:
   ```csharp
   [RunsFirst]
   public class AuthorizationInterceptor : IReadInterceptor
   {
       public TProperty ReadProperty<TProperty>(...)
       {
           _logger.LogDebug(
               "Checking {Permission} on {Property} for user {User} with roles {Roles}",
               "Read",
               context.Property.Name,
               AuthorizationContext.CurrentUser?.Identity?.Name,
               string.Join(", ", AuthorizationContext.ExpandedRoles));
           // ...
       }
   }
   ```

---

## Quick Reference

| Component | Location | Purpose |
|-----------|----------|---------|
| `AuthorizationContext` | Context/ | AsyncLocal for current user |
| `AuthorizationCircuitHandler` | Context/ | Sets context for Blazor activities |
| `AuthorizationContextMiddleware` | Context/ | Sets context for HTTP requests |
| `AuthorizationInterceptor` | Interceptors/ | Enforces permissions on properties |
| `IPermissionResolver` | Services/ | Determines required roles |
| `IRoleExpander` | Services/ | Expands role hierarchy |
| `HomeBlazeAuthenticationStateProvider` | Identity/ | Revalidates auth every 30 min |
| `IdentityDbContext` | Identity/Data/ | Stores users and roles |
| `[Permission]` | Attributes/ | Marks property permission level |
| `[DefaultPermissions]` | Attributes/ | Sets class-level defaults |
| `SubjectAuthorizationPanel` | Components/ | UI for permission editing |

---

## See Also

- [Implementation Plan](./2025-12-25-homeblaze-authorization-implementation.md)
- [Design Document](./2025-12-25-homeblaze-authorization-design.md)
- [ASP.NET Core Blazor DI](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection)
- [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity)
