# HomeBlaze Authorization System Documentation

This document provides a comprehensive overview of all authorization and authentication components, their locations, and responsibilities.

## Table of Contents

1. [Introduction](#introduction)
2. [Architecture Overview](#architecture-overview)
3. [Project Structure](#project-structure)
4. [Component Reference](#component-reference)
5. [Data Flow](#data-flow)
6. [Configuration](#configuration)
7. [Error Handling](#error-handling)
8. [Testing](#testing)
9. [Migration Guide](#migration-guide)
10. [Troubleshooting](#troubleshooting)
11. [Quick Reference](#quick-reference)

---

## Introduction

The HomeBlaze Authorization System provides fine-grained access control for subject properties and methods. It enables:

- **Role-based access control (RBAC)** with hierarchical roles (Admin → Supervisor → Operator → User → Guest → Anonymous)
- **Two-dimensional authorization model**: Kind (State/Configuration/Query/Operation) + Action (Read/Write/Invoke)
- **Per-property and per-subject permission overrides** stored in extension data
- **Multi-parent inheritance** with OR semantics
- **Defense-in-depth** with both UI filtering and interceptor enforcement

### Quick Start

1. **Add authorization to your subject context**:
   ```csharp
   var context = InterceptorSubjectContext
       .Create()
       .WithFullPropertyTracking()
       .WithAuthorization();  // Add authorization interceptor

   context.AddService(serviceProvider);  // Required for DI resolution
   ```

2. **Register services in DI**:
   ```csharp
   builder.Services.AddHomeBlazeAuthorization(options =>
   {
       options.UnauthenticatedRole = DefaultRoles.Anonymous;
   });
   ```

3. **Add middleware**:
   ```csharp
   app.UseAuthentication();
   app.UseMiddleware<AuthorizationContextMiddleware>();
   app.UseAuthorization();
   ```

4. **Decorate subjects with attributes**:
   ```csharp
   [InterceptorSubject]
   [SubjectAuthorize(Kind.State, AuthorizationAction.Read, DefaultRoles.Guest)]
   [SubjectAuthorize(Kind.State, AuthorizationAction.Write, DefaultRoles.Operator)]
   public partial class Device
   {
       [State]
       public partial bool IsOn { get; set; }

       [Configuration]
       [SubjectPropertyAuthorize(AuthorizationAction.Write, DefaultRoles.Admin)]
       public partial string ApiKey { get; set; }
   }
   ```

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
│  IAuthorizationResolver determines required roles for each property.       │
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
│   │   ├── AuthorizationEnums.cs         # Action and Kind enums, DefaultRoles class
│   │   ├── SubjectAuthorizeAttribute.cs  # [SubjectAuthorize(Kind, Action, roles)] on classes
│   │   ├── SubjectPropertyAuthorizeAttribute.cs # [SubjectPropertyAuthorize(Action, roles)] on properties
│   │   └── SubjectMethodAuthorizeAttribute.cs   # [SubjectMethodAuthorize(roles)] on methods
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
│   │   ├── IAuthorizationResolver.cs        # Interface for permission resolution
│   │   ├── AuthorizationResolver.cs         # Implementation with caching
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
        try
        {
            await UpdateAuthorizationContextAsync();  // Sets AsyncLocal
            await next(context);                       // Activity runs with context set
        }
        finally
        {
            _servicesAccessor.Services = null;  // Clear to prevent stale state
        }
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
        var resolver = GetAuthorizationResolver(context);
        var kind = GetPropertyKind(context.Property); // State or Configuration
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Read);

        if (!AuthorizationContext.HasAnyRole(requiredRoles))
            throw new UnauthorizedAccessException(...);

        return next(ref context);
    }

    private Kind GetPropertyKind(PropertyReference property)
    {
        var hasConfiguration = property.Metadata.PropertyInfo?
            .GetCustomAttribute<ConfigurationAttribute>() != null;
        return hasConfiguration ? Kind.Configuration : Kind.State;
    }
}
```

**Key Points**:
- Uses `[RunsFirst]` to execute before other interceptors
- Resolves `IAuthorizationResolver` from DI via `IServiceProvider`
- Throws `UnauthorizedAccessException` on denied access
- UI should filter first; interceptor is defense-in-depth

---

### 5. AuthorizedMethodInvoker

**Location**: `HomeBlaze.Authorization/Interceptors/AuthorizedMethodInvoker.cs`

**Purpose**: Decorator that enforces authorization on method invocations.

```csharp
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    private readonly ISubjectMethodInvoker _inner;
    private readonly IAuthorizationResolver _resolver;

    public async Task<MethodInvocationResult> InvokeAsync(
        IInterceptorSubject subject,
        SubjectMethodInfo method,
        object?[] parameters,
        CancellationToken cancellationToken = default)
    {
        var kind = method.Kind == SubjectMethodKind.Query ? Kind.Query : Kind.Operation;
        var requiredRoles = _resolver.ResolveMethodRoles(subject, method.Name, kind);

        if (!AuthorizationContext.HasAnyRole(requiredRoles))
            throw new UnauthorizedAccessException($"Cannot invoke {method.Name}");

        return await _inner.InvokeAsync(subject, method, parameters, cancellationToken);
    }
}
```

**Key Points**:
- Wraps the existing `ISubjectMethodInvoker` as a decorator
- Kind is `Query` for read-only methods, `Operation` for mutating methods
- Action is always `Invoke` for methods

---

### 6. AuthorizationResolver

**Location**: `HomeBlaze.Authorization/Services/AuthorizationResolver.cs`

**Purpose**: Determines which roles are required for a given property/method with Kind+Action.

```csharp
public class AuthorizationResolver : IAuthorizationResolver
{
    public string[] ResolvePropertyRoles(PropertyReference property, Kind kind, AuthorizationAction action)
    {
        // 1. Check property-level override in ExtData
        if (TryGetPropertyRoles(property, kind, action, out var roles))
            return roles;

        // 2. Check subject-level override in ExtData
        if (TryGetSubjectRoles(property.Subject, kind, action, out roles))
            return roles;

        // 3. Check [SubjectPropertyAuthorize] attribute on property
        if (TryGetPropertyAttributeRoles(property, action, out roles))
            return roles;

        // 4. Check [SubjectAuthorize] attribute on class
        if (TryGetSubjectAttributeRoles(property.Subject, kind, action, out roles))
            return roles;

        // 5. Walk parent hierarchy (OR combine)
        if (TryGetInheritedRoles(property.Subject, kind, action, out roles))
            return roles;

        // 6. Fall back to global defaults
        return _options.DefaultPermissionRoles[(kind, action)];
    }

    public string[] ResolveMethodRoles(IInterceptorSubject subject, string methodName, Kind kind)
    {
        // Similar resolution for methods (Action is always Invoke)
        // Kind is determined from [Query] or [Operation] attribute
        // ...
    }
}
```

**Resolution Order** (local overrides before inheritance):
1. Property ExtData override (runtime UI override) - Key: `HomeBlaze.Authorization:{Kind}:{Action}`
2. Subject ExtData override (runtime UI override)
3. `[SubjectPropertyAuthorize(Action, roles)]` attribute on property
4. `[SubjectAuthorize(Kind, Action, roles)]` attribute on class
5. Parent subject inheritance (OR combine across branches)
6. Global defaults from `AuthorizationOptions` by (Kind, AuthorizationAction) tuple

---

### 7. RoleExpander

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
- Stored in `RoleComposition` table in database (seeded on first run, editable via Admin UI)
- NOT stored in `appsettings.json` - database is the source of truth for role hierarchy
- Editable via Admin UI (`RoleConfiguration.razor`)
- Default hierarchy: Admin → Supervisor → Operator → User → Guest → Anonymous

---

### 8. HomeBlazeAuthenticationStateProvider

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

### 9. Authorization Attributes

**Location**: `HomeBlaze.Authorization/Attributes/`

```csharp
// Enums and constants
public enum AuthorizationAction { Read, Write, Invoke }
public enum Kind { State, Configuration, Query, Operation }

public static class DefaultRoles
{
    public const string Anonymous = "Anonymous";
    public const string Guest = "Guest";
    public const string User = "User";
    public const string Operator = "Operator";
    public const string Supervisor = "Supervisor";
    public const string Admin = "Admin";
}

// On classes - define defaults by Kind and Action
[SubjectAuthorize(Kind.State, AuthorizationAction.Read, DefaultRoles.Guest)]
[SubjectAuthorize(Kind.State, AuthorizationAction.Write, DefaultRoles.Operator)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Read, DefaultRoles.User)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Write, DefaultRoles.Supervisor)]
[SubjectAuthorize(Kind.Operation, AuthorizationAction.Invoke, DefaultRoles.Operator)]
public partial class Device { }

// On properties - override for specific action (Kind inferred from [State]/[Configuration])
[Configuration]
[SubjectPropertyAuthorize(AuthorizationAction.Read, DefaultRoles.Guest)]  // Override: Guest can read config
public partial string DisplayName { get; set; }

[Configuration]
[SubjectPropertyAuthorize(AuthorizationAction.Write, DefaultRoles.Admin)] // Override: Only Admin can write
public partial string MacAddress { get; set; }

// On methods - override for Invoke action (Kind inferred from [Query]/[Operation])
[Operation]
[SubjectMethodAuthorize(DefaultRoles.Admin)]  // Only Admin can invoke
public void FactoryReset() { }
```

---

### 10. Subject Extension Data

**Location**: Stored in `Subject.Data` dictionary, serialized with subject

```csharp
// Subject-level permission override by Kind and Action
subject.SetPermissionRoles("", Kind.State, AuthorizationAction.Read, [DefaultRoles.Operator]);
subject.SetPermissionRoles("", Kind.Configuration, AuthorizationAction.Write, [DefaultRoles.Admin]);

// Property-level permission override
subject.SetPermissionRoles("MacAddress", Kind.Configuration, AuthorizationAction.Read, [DefaultRoles.Admin]);

// Check current permissions
var roles = subject.GetPermissionRoles("MacAddress", Kind.Configuration, AuthorizationAction.Read);
```

**Storage Format** (using `Kind:Action` keys with inherit flag):
```json
{
  "$type": "SecuritySystem",
  "$authorization": {
    "": {
      "State:Read": { "inherit": false, "roles": ["SecurityGuard"] },
      "State:Write": { "inherit": false, "roles": ["Admin"] },
      "Operation:Invoke": { "inherit": false, "roles": ["Admin"] }
    },
    "ArmCode": {
      "Configuration:Read": { "inherit": false, "roles": ["Admin"] },
      "Configuration:Write": { "inherit": false, "roles": ["Admin"] }
    }
  }
}
```

**AuthorizationOverride Schema**:
- `inherit: false` → Replace: Only these roles can access
- `inherit: true` → Extend: These roles in addition to inherited roles

**Internal Data Key Format**: `HomeBlaze.Authorization:{Kind}:{Action}` (e.g., `HomeBlaze.Authorization:State:Read`)

For complete JSON schema with descendant paths, see [Design Document](./2025-12-25-homeblaze-authorization-design.md#26-authorization-data-persistence).

---

### 11. UI Components

**Location**: `HomeBlaze.Components/Authorization/`

#### AuthorizationContextProvider.razor
Wraps the entire app to ensure `CascadingAuthenticationState` is available:
```razor
<CascadingAuthenticationState>
    @ChildContent
</CascadingAuthenticationState>
```

#### SubjectAuthorizationPane.razor
Right pane for editing subject-level permissions by Kind+Action:
```razor
<SubjectAuthorizationPane Subject="@subject" />
```

#### PropertyAuthorizationPane.razor
Right pane for editing property-level permission overrides:
```razor
<PropertyAuthorizationPane Property="@property" />
```

#### MethodAuthorizationPane.razor
Right pane for editing method-level permission overrides:
```razor
<MethodAuthorizationPane Subject="@subject" MethodName="@methodName" />
```

#### Permission Checking in Components
```razor
@inject IAuthorizationResolver AuthorizationResolver

@if (CanRead(property))
{
    <div class="property-row">
        <PropertyEditor Property="@property" ReadOnly="@(!CanWrite(property))" />
        <button class="auth-icon @(HasOverride(property) ? "overridden" : "")"
                @onclick="() => OpenPropertyAuthPane(property)">🔒</button>
    </div>
}

@code {
    private Kind GetPropertyKind(PropertyReference property)
    {
        var hasConfig = property.Metadata.PropertyInfo?
            .GetCustomAttribute<ConfigurationAttribute>() != null;
        return hasConfig ? Kind.Configuration : Kind.State;
    }

    private bool CanRead(PropertyReference property) =>
        AuthorizationContext.HasAnyRole(
            AuthorizationResolver.ResolvePropertyRoles(property, GetPropertyKind(property), AuthorizationAction.Read));

    private bool CanWrite(PropertyReference property) =>
        AuthorizationContext.HasAnyRole(
            AuthorizationResolver.ResolvePropertyRoles(property, GetPropertyKind(property), AuthorizationAction.Write));

    private bool HasOverride(PropertyReference property) =>
        property.Subject.Data.Keys.Any(k =>
            k.property == property.Name &&
            k.key.StartsWith("HomeBlaze.Authorization:"));
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
   ├── Resolve required roles via IAuthorizationResolver
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

## Error Handling

### Catching Authorization Exceptions

When a user attempts an operation they're not authorized for, the `AuthorizationInterceptor` throws `UnauthorizedAccessException`. Handle this gracefully in your UI:

```csharp
try
{
    subject.ProtectedProperty = newValue;
}
catch (UnauthorizedAccessException ex)
{
    // Log for security audit
    _logger.LogWarning("Authorization denied for {User}: {Message}",
        AuthorizationContext.CurrentUser?.Identity?.Name, ex.Message);

    // Show user-friendly message
    Snackbar.Add("You don't have permission to modify this property.", Severity.Warning);
}
```

### Best Practices

1. **Filter at UI level first** - Don't show controls users can't use
2. **Interceptor is defense-in-depth** - Exception should be rare if UI is correct
3. **Log security events** - Track denied access attempts for audit
4. **Use generic messages** - Don't reveal security details to end users

### Handling Malformed Authorization Data

If `$authorization` JSON is malformed during deserialization:

```csharp
// AuthorizationDataProvider handles this gracefully
public void SetAdditionalProperties(IInterceptorSubject subject, Dictionary<string, JsonElement> properties)
{
    try
    {
        var overrides = element.Deserialize<...>();
        // ... apply overrides
    }
    catch (JsonException ex)
    {
        _logger.LogWarning("Invalid authorization data for subject: {Error}", ex.Message);
        // Subject loads without authorization overrides - defaults apply
    }
}
```

---

## Testing

### Unit Testing with Mocked Authorization Context

```csharp
public class AuthorizationTests
{
    [Fact]
    public void AdminCanWriteProtectedProperty()
    {
        // Arrange - Set up admin user context
        AuthorizationContext.SetUser(
            CreateUserPrincipal("admin"),
            ["Admin", "Supervisor", "Operator", "User", "Guest", "Anonymous"]);

        var context = CreateTestContext();
        var subject = new TestDevice(context);

        // Act & Assert - Should not throw
        subject.ProtectedProperty = "new value";
        Assert.Equal("new value", subject.ProtectedProperty);
    }

    [Fact]
    public void GuestCannotWriteProtectedProperty()
    {
        // Arrange - Set up guest user context
        AuthorizationContext.SetUser(
            CreateUserPrincipal("guest"),
            ["Guest", "Anonymous"]);

        var context = CreateTestContext();
        var subject = new TestDevice(context);

        // Act & Assert - Should throw
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            subject.ProtectedProperty = "hacked";
        });
    }

    private ClaimsPrincipal CreateUserPrincipal(string name)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, name)], "Test"));
    }

    private IInterceptorSubjectContext CreateTestContext()
    {
        var services = new ServiceCollection();
        services.AddHomeBlazeAuthorization();
        var sp = services.BuildServiceProvider();

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithAuthorization();

        context.AddService(sp);
        return context;
    }
}
```

### Testing AsyncLocal Context Flow

```csharp
[Fact]
public async Task AuthorizationContext_FlowsAcrossAwait()
{
    // Arrange
    var user = CreateUserPrincipal("testuser");
    AuthorizationContext.SetUser(user, ["User", "Guest", "Anonymous"]);

    // Act - Verify context flows through async operations
    ClaimsPrincipal? capturedUser = null;
    await Task.Run(async () =>
    {
        await Task.Delay(10);
        capturedUser = AuthorizationContext.CurrentUser;
    });

    // Assert
    Assert.NotNull(capturedUser);
    Assert.Equal("testuser", capturedUser.Identity?.Name);
}
```

### Integration Testing with Test Server

```csharp
public class AuthorizationIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    [Fact]
    public async Task AnonymousUser_CannotAccessAdminEndpoint()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedAdmin_CanAccessAdminEndpoint()
    {
        var client = _factory.CreateClient();
        // Add authentication cookie/token
        client.DefaultRequestHeaders.Add("Cookie", "auth=admin-token");

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
```

---

## Migration Guide

### Migrating from No Authorization

If your existing HomeBlaze installation has no authorization, follow these steps:

1. **Existing subjects work unchanged** - Without `[SubjectAuthorize]` attributes, global defaults apply
2. **Add authorization incrementally**:
   ```csharp
   // Start with permissive defaults
   options.DefaultPermissionRoles = new()
   {
       [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Anonymous],
       [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Guest],
       // ...
   };
   ```

3. **Add attributes to sensitive subjects first**:
   ```csharp
   [SubjectAuthorize(Kind.Configuration, AuthorizationAction.Write, DefaultRoles.Admin)]
   public partial class SecuritySystem { }
   ```

4. **Test with a non-admin user** before tightening defaults

### Serialization Compatibility

- **Old JSON without `$authorization`**: Loads normally, defaults apply
- **New JSON with `$authorization`**: Requires `AuthorizationDataProvider` registered

```csharp
// Ensure provider is registered for deserialization
services.AddSingleton<ISubjectDataProvider, AuthorizationDataProvider>();
```

### Database Migration

On first run, `IdentitySeeding` automatically:
1. Creates the Identity database schema
2. Seeds default roles (Anonymous → Guest → User → Operator → Supervisor → Admin)
3. Creates a default admin user with no password (`MustChangePassword = true`)

No manual migration scripts required.

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
   var resolver = serviceProvider.GetRequiredService<IAuthorizationResolver>();
   // Specify Kind and Action for the property
   var kind = Kind.State; // or Kind.Configuration based on attribute
   var roles = resolver.ResolvePropertyRoles(property, kind, AuthorizationAction.Read);
   Console.WriteLine($"Required roles for {kind}:{AuthorizationAction.Read}: {string.Join(", ", roles)}");
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
   var resolver = serviceProvider.GetRequiredService<IAuthorizationResolver>();
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
           var kind = GetPropertyKind(context.Property);
           _logger.LogDebug(
               "Checking {Kind}:{Action} on {Property} for user {User} with roles {Roles}",
               kind,
               AuthorizationAction.Read,
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
| `AuthorizedMethodInvoker` | Interceptors/ | Enforces permissions on method invocations |
| `IAuthorizationResolver` | Services/ | Determines required roles by Kind+Action |
| `IRoleExpander` | Services/ | Expands role hierarchy |
| `ISubjectDataProvider` | Abstractions/ | Interface for serialization extensibility |
| `AuthorizationDataProvider` | Data/ | Serializes `$authorization` to/from JSON |
| `AuthorizationOverride` | Data/ | Storage model for permission overrides |
| `AuthorizationOptions` | Options/ | Configuration (defaults, unauthenticated role) |
| `HomeBlazeAuthenticationStateProvider` | Identity/ | Revalidates auth every 30 min |
| `IdentityDbContext` | Identity/Data/ | Stores users and roles |
| `RoleComposition` | Identity/Data/ | Role hierarchy entity (database table) |
| `AuthorizationAction` enum | Attributes/ | Read, Write, Invoke |
| `Kind` enum | Attributes/ | State, Configuration, Query, Operation |
| `DefaultRoles` class | Attributes/ | Anonymous, Guest, User, Operator, Supervisor, Admin |
| `[SubjectAuthorize]` | Attributes/ | Class-level defaults by Kind+Action |
| `[SubjectPropertyAuthorize]` | Attributes/ | Property-level role override |
| `[SubjectMethodAuthorize]` | Attributes/ | Method-level role override |
| `SubjectAuthorizationPane` | Components/ | Right pane for subject permissions |
| `PropertyAuthorizationPane` | Components/ | Right pane for property permissions |
| `MethodAuthorizationPane` | Components/ | Right pane for method permissions |

---

## See Also

- [Implementation Plan](./2025-12-25-homeblaze-authorization-implementation.md)
- [Design Document](./2025-12-25-homeblaze-authorization-design.md)
- [ASP.NET Core Blazor DI](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection)
- [ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity)
