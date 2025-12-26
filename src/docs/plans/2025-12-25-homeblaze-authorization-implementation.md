# HomeBlaze Authorization - Implementation Plan

**Date**: 2025-12-25
**Design Document**: `2025-12-25-homeblaze-authorization-design.md`

## Implementation Decisions

| Question | Decision |
|----------|----------|
| Project location | Standalone `HomeBlaze.Authorization` project |
| Test framework | Both unit and integration tests |
| OAuth packages | Only `Microsoft.AspNetCore.Authentication.OpenIdConnect` |
| Scoped service access | Via `IServiceProvider` → `IHttpContextAccessor`, no user = no restrictions |
| Unauthorized read | Filter at UI + throw exception if read bypasses UI |
| Role storage | Database (runtime changes via admin UI) |
| Connection string | `ConnectionStrings:IdentityDatabase` |
| Terminology | "Access Level" renamed to "Permission" |

---

## Defaults Used

| Setting | Source | Value |
|---------|--------|-------|
| Password length | Identity default | 6 characters |
| Password complexity | Identity default | Digit + upper + lower + special |
| Lockout attempts | Identity default | 5 attempts |
| Lockout duration | Identity default | 5 minutes |
| Cookie expiration | Identity default | 14 days, sliding |
| Concurrent sessions | Identity default | Allowed |
| HTTPS | ASP.NET Core | `UseHttpsRedirection()` - only redirects if HTTPS configured |

**No open questions - all resolved with framework defaults.**

---

## Phase 1: Core Infrastructure

### Task 1: Create Project Structure

**Summary**: Set up HomeBlaze.Authorization and HomeBlaze.Authorization.Blazor projects

**Implementation**:
1. Create `src/HomeBlaze/HomeBlaze.Authorization/HomeBlaze.Authorization.csproj` (class library, net9.0)
2. Create `src/HomeBlaze/HomeBlaze.Authorization.Blazor/HomeBlaze.Authorization.Blazor.csproj` (Razor class library)
3. Create `src/HomeBlaze/HomeBlaze.Authorization.Tests/` (xUnit)
4. Add project references to solution
5. Create folder structure:
   - Configuration/
   - Roles/
   - Resolution/
   - Attributes/
   - Extensions/
   - Data/

**Subtasks**:
- [ ] Tests: Create test project with basic smoke test
- [ ] Docs: N/A
- [ ] Code quality: Match existing project conventions (nullable enabled, implicit usings)
- [ ] Dependencies:
  - `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
  - `Microsoft.EntityFrameworkCore.Sqlite`
  - `Microsoft.AspNetCore.Authentication.OpenIdConnect`
- [ ] Performance: N/A

---

### Task 1b: Create ISubjectDataProvider Interface

**Summary**: Extensibility interface for serializing additional subject data (like permissions)

**Location**: `HomeBlaze.Services`

**Implementation**:
1. Create `ISubjectDataProvider` interface:
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

**Why "$" Prefix**:
- Prevents conflicts with `[Configuration]` properties
- Follows existing `$type` convention
- Clear namespace separation in JSON

**Subtasks**:
- [ ] Tests: Interface contract tests
- [ ] Docs: XML documentation
- [ ] Code quality: N/A
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

### Task 1c: Update ConfigurableSubjectSerializer

**Summary**: Add ISubjectDataProvider support to serializer

**Location**: `HomeBlaze.Services/ConfigurableSubjectSerializer.cs`

**Implementation**:
1. Add `IEnumerable<ISubjectDataProvider>` constructor parameter
2. In `Serialize()`:
   - After writing `[Configuration]` properties, iterate providers
   - Call `GetAdditionalProperties()` on each provider
   - Write each returned property with `$` prefix
3. In `Deserialize()`:
   - Collect all `$`-prefixed properties (except `$type`)
   - Strip `$` prefix from keys
   - Pass dictionary to each provider's `SetAdditionalProperties()`

```csharp
public ConfigurableSubjectSerializer(
    TypeProvider typeProvider,
    IServiceProvider serviceProvider,
    IEnumerable<ISubjectDataProvider> dataProviders)  // NEW
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
```

**Note**: If no providers are registered, `IEnumerable<ISubjectDataProvider>` is empty and behavior is unchanged.

**Subtasks**:
- [ ] Tests: Serialization round-trip tests with provider
- [ ] Docs: N/A
- [ ] Code quality: Backwards compatible
- [ ] Dependencies: No new dependencies
- [ ] Performance: Provider iteration is O(n) where n = number of providers

---

### Task 2: Implement AuthorizationOptions

**Summary**: Configuration class for authorization settings (use Identity defaults for password/session)

**Implementation**:
1. Create `AuthorizationOptions.cs`:
   ```csharp
   public class AuthorizationOptions
   {
       public string UnauthenticatedRole { get; set; } = "Anonymous";
       public Dictionary<string, string[]> DefaultPermissionRoles { get; set; } = new()
       {
           ["Read"] = ["Anonymous"],
           ["Write"] = ["User"],
           ["Invoke"] = ["User"]
       };
   }
   ```
2. Create `OAuthProviderOptions.cs` for OIDC configuration (provider name, client id/secret)

**Note**: Password/session options use ASP.NET Core Identity defaults - no custom classes needed.

**Subtasks**:
- [ ] Tests: Options binding tests
- [ ] Docs: Document configuration schema
- [ ] Code quality: Use records where immutable
- [ ] Dependencies: `Microsoft.Extensions.Options`
- [ ] Performance: N/A

---

## Phase 2: Identity & Database

### Task 3: Set Up ApplicationUser and DbContext

**Summary**: Extend IdentityUser with custom properties and all required entities

**Implementation**:
1. Create `ApplicationUser : IdentityUser` with:
   - DisplayName
   - CreatedAt
   - LastLoginAt
   - MustChangePassword
2. Create `RoleComposition` entity:
   ```csharp
   public class RoleComposition
   {
       public int Id { get; set; }
       public string RoleName { get; set; } = "";
       public string IncludesRole { get; set; } = "";
   }
   ```
3. Create `ExternalRoleMapping` entity:
   ```csharp
   public class ExternalRoleMapping
   {
       public int Id { get; set; }
       public string Provider { get; set; } = "";
       public string ExternalRole { get; set; } = "";
       public string InternalRole { get; set; } = "";
   }
   ```
4. Create `IdentityDbContext : IdentityDbContext<ApplicationUser>` with DbSets for above
5. Configure connection string from appsettings
6. Add initial EF Core migration

**Subtasks**:
- [ ] Tests: DbContext smoke tests, migration tests
- [ ] Docs: Document schema
- [ ] Code quality: Proper nullable annotations
- [ ] Dependencies: Already added
- [ ] Performance: Index on RoleName, Provider+ExternalRole
- [ ] Security: No sensitive data in logs, password hashing handled by Identity

**Edge cases**:
- First run with no database → auto-create via EnsureCreated or migrations

---

### Task 4: Implement IRoleExpander

**Summary**: Core role expansion service with caching and reload capability

**Implementation**:
1. Create `IRoleExpander` interface:
   ```csharp
   public interface IRoleExpander
   {
       IReadOnlySet<string> ExpandRoles(IEnumerable<string> roles);
       Task ReloadAsync(); // Reload composition from database
   }
   ```
2. Implement `RoleExpander` with:
   - Load role composition from `RoleComposition` table
   - Cache composition in `ConcurrentDictionary`
   - Recursive expansion with cycle detection (visited set)
   - Return `ImmutableHashSet<string>` for O(1) lookups

**Subtasks**:
- [ ] Tests:
  - Simple expansion (Admin → [Admin, User, Guest, Anonymous])
  - Circular detection (A→B→A throws)
  - Unknown role handling (pass through)
  - Empty input → empty set
  - ReloadAsync clears cache
- [ ] Docs: N/A
- [ ] Code quality: Thread-safe, immutable results
- [ ] Dependencies: No new dependencies
- [ ] Performance: Cache composition, expand on demand

**Edge cases**:
- Circular role references → throw with clear message
- Self-referencing role → ignore self-reference
- Unknown role → include as-is (external roles)

---

### Task 4b: Implement RoleHierarchyClaimsTransformation

**Summary**: ASP.NET Core integration - adds expanded role claims for standard auth checks

**Implementation**:
1. Create `RoleHierarchyClaimsTransformation : IClaimsTransformation`
2. Inject `IRoleExpander`
3. Get user's role claims, expand via `IRoleExpander`, add as new claims

```csharp
public class RoleHierarchyClaimsTransformation : IClaimsTransformation
{
    private readonly IRoleExpander _expander;

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value);
        var expanded = _expander.ExpandRoles(roles);

        // Add missing roles as new claims
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var role in expanded)
        {
            if (!principal.IsInRole(role))
                identity.AddClaim(new Claim(ClaimTypes.Role, role));
        }
        return Task.FromResult(principal);
    }
}
```

**Subtasks**:
- [ ] Tests: Claims transformation tests
- [ ] Docs: N/A
- [ ] Code quality: Null-safe
- [ ] Dependencies: No new dependencies
- [ ] Performance: Delegates to cached IRoleExpander

---

### Task 5: Implement IdentitySeeding

**Summary**: Seed default roles AND admin user on first startup

**Implementation**:
1. Create `IdentitySeeding : IHostedService`
2. Seed default roles in AspNetRoles + RoleComposition:
   - Anonymous = []
   - Guest = [Anonymous]
   - User = [Guest]
   - Admin = [User]
3. Check if any users exist
4. Create admin with no password, `MustChangePassword = true`
5. Assign Admin role to seeded user

**Subtasks**:
- [ ] Tests: Seeding tests (empty db, existing users, idempotency)
- [ ] Docs: Document first-run behavior
- [ ] Code quality: Async/await best practices
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

**Security**:
- [ ] Log warning about default admin creation

**Edge cases**:
- Race condition → use database transaction or idempotent insert

---

### Task 6: Configure Identity and Cookie Authentication

**Summary**: Wire up ASP.NET Core Identity with cookie auth

**Implementation**:
1. Create `AddHomeBlazeIdentity()` extension method
2. Register IdentityDbContext
3. Register Identity with ApplicationUser
4. Configure cookie paths (/login, /access-denied)
5. Register `IRoleExpander` (singleton)
6. Register `RoleHierarchyClaimsTransformation` (scoped)
7. Add `IHttpContextAccessor` registration

**Note**: Use Identity defaults for password/lockout/session settings.

**Subtasks**:
- [ ] Tests: Integration tests for login flow
- [ ] Docs: N/A
- [ ] Code quality: Options pattern
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A
- [ ] Security: HttpOnly + Secure + SameSite cookies

---

### Task 7: Configure OAuth/OIDC Providers

**Summary**: Register OIDC providers from configuration

**Implementation**:
1. Create `AddHomeBlazeOAuth()` extension method
2. Read OAuth providers from appsettings
3. Register OpenIdConnect authentication for each configured provider
4. Map external claims to internal roles via ExternalRoleMapping table

**Subtasks**:
- [ ] Tests: Conditional registration tests, role mapping tests
- [ ] Docs: OAuth configuration guide
- [ ] Code quality: Fail fast on invalid config
- [ ] Dependencies: Already added
- [ ] Performance: N/A
- [ ] Security: Validate redirect URIs, use state parameter, validate tokens

**Edge cases**:
- No OAuth config → skip registration (local accounts only)
- Invalid configuration → log error, skip provider

---

## Phase 3: Authorization Resolution

### Task 8: Create Permission Attributes

**Summary**: Attributes for compile-time permission and role definitions

**Implementation**:
1. Create `[Permission("Read", Roles = new[] { "Guest" })]` attribute for properties/methods
2. Create `[DefaultPermissions("Read", "Guest")]` attribute for classes
3. Configure proper `AttributeUsage`

**Note**: Attributes should live in `HomeBlaze.Abstractions` to avoid circular dependencies.

**Subtasks**:
- [ ] Tests: Attribute parsing via reflection
- [ ] Docs: Usage examples in XML comments
- [ ] Code quality: Allow multiple on same target
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A (reflection cached in resolver)

---

### Task 8b: Implement PermissionsDataProvider

**Summary**: Serialization provider for permission data persistence

**Location**: `HomeBlaze.Authorization`

**Implementation**:
1. Create `PermissionsDataProvider : ISubjectDataProvider`
2. Use `HomeBlaze.Authorization:` prefix for Subject.Data keys (Namotion convention)
3. Serialize permissions to `$permissions` JSON property
4. Register as `ISubjectDataProvider` in DI

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

**JSON Output**:
```json
{
  "$type": "HomeBlaze.Samples.SecuritySystem",
  "name": "Security",
  "$permissions": {
    "": { "Read": ["SecurityGuard"], "Write": ["Admin"] },
    "ArmCode": { "Read": ["Admin"] }
  }
}
```

**Registration**:
```csharp
// In AddHomeBlazeAuthorization()
services.AddSingleton<ISubjectDataProvider, PermissionsDataProvider>();
```

**Subtasks**:
- [ ] Tests: Round-trip serialization tests
- [ ] Docs: N/A
- [ ] Code quality: Use constants for key prefix
- [ ] Dependencies: Depends on Task 1b (ISubjectDataProvider)
- [ ] Performance: N/A

---

### Task 9: Implement PermissionResolver

**Summary**: Core resolution logic with inheritance traversal and caching

**Implementation**:
1. Create `IPermissionResolver` interface
2. Implement 6-level priority chain:
   - Property ExtData
   - Property Attribute
   - Subject ExtData
   - Subject Attribute
   - Parent traversal (OR combine)
   - Global defaults
3. Implement `TraverseParents()` with OR combination and visited set
4. **Cache resolved permissions per (subject, property, permission)** - critical for performance

```csharp
public interface IPermissionResolver
{
    string[] ResolvePropertyRoles(PropertyReference property, string permission);
    string[] ResolveSubjectRoles(IInterceptorSubject subject, string permission);
    void InvalidateCache(IInterceptorSubject subject); // Call on ExtData change
}
```

**Subtasks**:
- [ ] Tests:
  - Priority chain tests
  - Multi-parent OR combination
  - Global defaults fallback
  - Circular parent handling
  - Cache invalidation
- [ ] Docs: Document resolution algorithm
- [ ] Code quality: Avoid allocations in hot path
- [ ] Dependencies: `Namotion.Interceptor.Tracking`
- [ ] Performance:
  - **Cache per (subject, propertyName, permission) using ConcurrentDictionary**
  - Cache attribute lookups per type (FrozenDictionary at startup)
  - Use pooled HashSet for parent traversal
  - Return ImmutableArray<string> instead of string[]

**Edge cases**:
- Circular parent references → track visited subjects
- Empty role arrays → treat as "defined" (blocks inheritance)

---

### Task 10: Implement Authorization Interceptor

**Summary**: Single interceptor implementing both read and write access control

**Implementation**:
1. Create `AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor` with **`[RunsFirst]`** attribute
   - Read: Throw `UnauthorizedAccessException` on denied read (defense in depth)
   - Write: Throw `UnauthorizedAccessException` on denied write (defense in depth)
   - UI should filter - interceptor is last line of defense
2. Resolve `IPermissionResolver` via DI (`IServiceProvider` from context)
3. Access user context via `AsyncLocal<ClaimsPrincipal>` (populated at request start)

```csharp
[RunsFirst] // Ensure auth check happens before any other interceptor
public class AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor
{
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ...)
    {
        // Resolve from DI via IServiceProvider
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IPermissionResolver>();
        if (resolver == null) return next(ref context); // Graceful degradation

        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, "Read");
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
            throw new UnauthorizedAccessException(...);

        return next(ref context);
    }
}
```

**Subtasks**:
- [ ] Tests: Block unauthorized, allow authorized, no-user scenario
- [ ] Docs: Integration documentation
- [ ] Code quality: Graceful degradation if services missing
- [ ] Dependencies: No new dependencies
- [ ] Performance:
  - **Use cached resolution from Task 9**
  - Check user roles via HashSet (O(1) lookup)
  - No allocations in hot path
- [ ] Security: Don't leak sensitive info in exception messages

---

### Task 11: Implement AuthorizedMethodInvoker

**Summary**: Decorator for ISubjectMethodInvoker that checks Invoke permission

**Implementation**:
1. Create `AuthorizedMethodInvoker : ISubjectMethodInvoker`
2. Wrap existing invoker, check permission before delegating
3. Use same resolution logic as property authorization

```csharp
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    private readonly ISubjectMethodInvoker _inner;
    private readonly IPermissionResolver _resolver;

    public async Task<MethodInvocationResult> InvokeAsync(...)
    {
        var requiredRoles = _resolver.ResolveMethodRoles(subject, method.Name);
        // Check, then delegate to _inner
    }
}
```

**Subtasks**:
- [ ] Tests: Method authorization tests
- [ ] Docs: N/A
- [ ] Code quality: Decorator pattern
- [ ] Dependencies: No new dependencies
- [ ] Performance: Cache method permissions per (Type, methodName) at startup

---

### Task 12: Implement Authorization Context for Interceptors

**Summary**: Populate AsyncLocal with current user using official .NET 8+ `CreateInboundActivityHandler` pattern

**Reference**: [ASP.NET Core Blazor dependency injection - Access server-side Blazor services from different DI scope](https://learn.microsoft.com/en-us/aspnet/core/blazor/fundamentals/dependency-injection?view=aspnetcore-9.0)

**Implementation**:

1. Create `AuthorizationContext` static class with `AsyncLocal`
2. Create `CircuitServicesAccessor` for scoped service access
3. Create `AuthorizationCircuitHandler` using `CreateInboundActivityHandler`
4. Create middleware for HTTP/prerendering requests

```csharp
/// <summary>
/// Static context for authorization - uses AsyncLocal to flow user context
/// through the call stack including to interceptors.
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
        var userRoles = ExpandedRoles;
        return requiredRoles.Any(r => userRoles.Contains(r));
    }
}
```

**Pattern 1: CircuitServicesAccessor** (access scoped services from anywhere)

```csharp
/// <summary>
/// Provides access to circuit-scoped IServiceProvider from static/singleton code.
/// Uses AsyncLocal which is set by AuthorizationCircuitHandler.
/// </summary>
public class CircuitServicesAccessor
{
    private static readonly AsyncLocal<IServiceProvider?> _services = new();

    public IServiceProvider? Services
    {
        get => _services.Value;
        set => _services.Value = value;
    }
}
```

**Pattern 2: AuthorizationCircuitHandler** (the key component)

```csharp
/// <summary>
/// Circuit handler that sets authorization context for EVERY inbound activity.
/// Uses CreateInboundActivityHandler to wrap all circuit operations.
/// </summary>
public class AuthorizationCircuitHandler : CircuitHandler
{
    private readonly IServiceProvider _services;
    private readonly CircuitServicesAccessor _servicesAccessor;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IRoleExpander _roleExpander;
    private readonly AuthorizationOptions _options;

    public AuthorizationCircuitHandler(
        IServiceProvider services,
        CircuitServicesAccessor servicesAccessor,
        AuthenticationStateProvider authStateProvider,
        IRoleExpander roleExpander,
        AuthorizationOptions options)
    {
        _services = services;
        _servicesAccessor = servicesAccessor;
        _authStateProvider = authStateProvider;
        _roleExpander = roleExpander;
        _options = options;
    }

    /// <summary>
    /// Wraps EVERY inbound circuit activity (UI events, JS interop, renders).
    /// This ensures AuthorizationContext is always set during any operation.
    /// </summary>
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        return async context =>
        {
            // Set scoped services accessor
            _servicesAccessor.Services = _services;

            // Update authorization context with current user
            await UpdateAuthorizationContextAsync();

            // Execute the activity (event handler, render, etc.)
            await next(context);

            // Note: We don't clear AuthorizationContext here because
            // the user doesn't change during an activity
        };
    }

    /// <summary>
    /// Called when circuit opens - sets initial authorization context.
    /// </summary>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        _servicesAccessor.Services = _services;
        await UpdateAuthorizationContextAsync();
        await base.OnCircuitOpenedAsync(circuit, ct);
    }

    /// <summary>
    /// Called when circuit closes - cleanup.
    /// </summary>
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        _servicesAccessor.Services = null;
        return base.OnCircuitClosedAsync(circuit, ct);
    }

    private async Task UpdateAuthorizationContextAsync()
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        IEnumerable<string> roles;
        if (user.Identity?.IsAuthenticated != true)
        {
            // Unauthenticated users get the anonymous role
            roles = [_options.UnauthenticatedRole]; // "Anonymous"
        }
        else
        {
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value);
        }

        var expanded = _roleExpander.ExpandRoles(roles);
        AuthorizationContext.SetUser(user, expanded);
    }
}
```

**Pattern 3: HTTP Middleware** (for API requests and prerendering)

```csharp
/// <summary>
/// Middleware that sets AuthorizationContext for HTTP requests.
/// Required for: Web API calls, SSR/prerendering (before circuit exists).
/// </summary>
public class AuthorizationContextMiddleware
{
    private readonly RequestDelegate _next;

    public AuthorizationContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IRoleExpander roleExpander,
        AuthorizationOptions options)
    {
        var user = context.User;

        IEnumerable<string> roles;
        if (user.Identity?.IsAuthenticated != true)
        {
            roles = [options.UnauthenticatedRole];
        }
        else
        {
            roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value);
        }

        var expanded = roleExpander.ExpandRoles(roles);
        AuthorizationContext.SetUser(user, expanded);

        try
        {
            await _next(context);
        }
        finally
        {
            // Clear after request completes
            AuthorizationContext.SetUser(null, []);
        }
    }
}
```

**Pattern 4: RevalidatingServerAuthenticationStateProvider**

```csharp
/// <summary>
/// Revalidates user authentication every 30 minutes.
/// Detects role changes, password changes, user deletion.
/// </summary>
public class HomeBlazeAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;

    public HomeBlazeAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
        : base(loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(30);

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authState, CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var userManager = scope.ServiceProvider
            .GetRequiredService<UserManager<ApplicationUser>>();

        var user = await userManager.GetUserAsync(authState.User);
        if (user == null)
            return false; // User deleted

        // Verify security stamp hasn't changed (password/role change detection)
        var stamp = authState.User.FindFirstValue("AspNet.Identity.SecurityStamp");
        var currentStamp = await userManager.GetSecurityStampAsync(user);

        return stamp == currentStamp;
    }
}
```

**Service Registration**:

```csharp
public static class AuthorizationContextServiceExtensions
{
    public static IServiceCollection AddAuthorizationContext(this IServiceCollection services)
    {
        services.AddScoped<CircuitServicesAccessor>();
        services.AddScoped<CircuitHandler, AuthorizationCircuitHandler>();
        services.AddScoped<AuthenticationStateProvider, HomeBlazeAuthenticationStateProvider>();

        return services;
    }
}

// In Program.cs
builder.Services.AddAuthorizationContext();

// Middleware registration (BEFORE UseRouting)
app.UseMiddleware<AuthorizationContextMiddleware>();
```

**Flow Verification**:

```
HTTP Request (API/Prerender):
  └─► AuthorizationContextMiddleware sets AsyncLocal
      └─► Controller/Component runs with correct user

Blazor Circuit Activity (button click, etc.):
  └─► CreateInboundActivityHandler fires FIRST
      └─► Sets AsyncLocal with current user
          └─► Event handler runs
              └─► Component updates property
                  └─► Interceptor runs
                      └─► Reads AuthorizationContext.CurrentUser ✓
```

**Subtasks**:
- [ ] Tests: E2E tests verifying auth flows (see Task 12b)
- [ ] Docs: Document the dual-pattern approach
- [ ] Code quality: Handle all render modes (Server, WASM, Auto)
- [ ] Dependencies: `Microsoft.AspNetCore.Components.Authorization`
- [ ] Performance: HashSet for O(1) role checks
- [ ] Security: Anonymous role for unauthenticated

---

### Task 12b: Authorization Tests

**Summary**: Tests verifying authorization flows correctly in all scenarios.

**Test Distribution**:

| Test Type | Project | Purpose |
|-----------|---------|---------|
| E2E (Playwright) | `HomeBlaze.E2E.Tests` | Full UI flow tests |
| Unit Tests | `HomeBlaze.Services.Tests` | Service-level tests |
| Integration Tests | `HomeBlaze.Host.Services.Tests` | DI + interceptor integration |

---

#### E2E Tests (add to existing `HomeBlaze.E2E.Tests`)

**File**: `src/HomeBlaze/HomeBlaze.E2E.Tests/AuthorizationTests.cs`

```csharp
using HomeBlaze.E2E.Tests.Infrastructure;
using Microsoft.Playwright;

namespace HomeBlaze.E2E.Tests;

/// <summary>
/// E2E tests for authorization flows using Playwright.
/// Uses existing PlaywrightFixture and WebTestingHostFactory.
/// </summary>
[Collection(nameof(PlaywrightCollection))]
public class AuthorizationTests
{
    private readonly PlaywrightFixture _fixture;

    public AuthorizationTests(PlaywrightFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Anonymous_CanViewPublicSubjects()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Navigate to browser (no login)
        await page.GotoAsync(_fixture.ServerAddress + "browser");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Should see the subject browser
        var subjectList = page.Locator("[data-testid='subject-tree']");
        await Assertions.Expect(subjectList).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task Anonymous_CannotAccessAdminPages()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();

        // Act - Try to access admin page directly
        await page.GotoAsync(_fixture.ServerAddress + "admin/users");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Should redirect to login or show unauthorized
        await page.WaitForURLAsync(url =>
            url.Contains("/login") || url.Contains("/unauthorized"),
            new() { Timeout = 30000 });
    }

    [Fact]
    public async Task Login_ShouldUpdateUI()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress);
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Login as admin
        await LoginAsAdmin(page);

        // Assert - Should see admin-only content
        var adminLink = page.GetByRole(AriaRole.Link, new() { Name = "Admin" });
        await Assertions.Expect(adminLink).ToBeVisibleAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task Logout_ShouldRevokeAccess()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await LoginAsAdmin(page);

        // Verify logged in
        var adminLink = page.GetByRole(AriaRole.Link, new() { Name = "Admin" });
        await Assertions.Expect(adminLink).ToBeVisibleAsync(new() { Timeout = 30000 });

        // Act - Logout
        await page.ClickAsync("[data-testid='logout-button']");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - Admin link should be hidden
        await Assertions.Expect(adminLink).ToBeHiddenAsync(new() { Timeout = 30000 });
    }

    [Fact]
    public async Task SubjectProperty_RespectsWritePermission()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await page.GotoAsync(_fixture.ServerAddress + "browser");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Navigate to a subject
        await page.ClickAsync("[data-testid='subject-tree'] >> text=Motor");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Assert - As anonymous, property inputs should be read-only
        var propertyInput = page.Locator("[data-property='Name'] input");
        if (await propertyInput.CountAsync() > 0)
        {
            await Assertions.Expect(propertyInput).ToBeDisabledAsync();
        }
    }

    [Fact]
    public async Task LazyRenderedContent_MaintainsAuthContext()
    {
        // Arrange
        var page = await _fixture.CreatePageAsync();
        await LoginAsAdmin(page);
        await page.GotoAsync(_fixture.ServerAddress + "browser");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        // Act - Expand a collapsed section (lazy rendered)
        var expandButton = page.Locator("[data-testid='expand-details']").First;
        if (await expandButton.CountAsync() > 0)
        {
            await expandButton.ClickAsync();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            // Assert - Lazy content should respect admin permissions
            var editButton = page.Locator("[data-testid='edit-button']").First;
            if (await editButton.CountAsync() > 0)
            {
                await Assertions.Expect(editButton).ToBeEnabledAsync();
            }
        }
    }

    private async Task LoginAsAdmin(IPage page)
    {
        await page.GotoAsync(_fixture.ServerAddress + "login");
        await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

        await page.FillAsync("[name='username']", "admin");
        await page.FillAsync("[name='password']", "admin123");
        await page.ClickAsync("button[type='submit']");

        // Wait for redirect after login
        await page.WaitForURLAsync(url => !url.Contains("/login"), new() { Timeout = 30000 });
    }
}
```

---

#### Unit Tests (add to existing `HomeBlaze.Services.Tests`)

**File**: `src/HomeBlaze/HomeBlaze.Services.Tests/Authorization/RoleExpanderTests.cs`

```csharp
using HomeBlaze.Authorization.Services;
using HomeBlaze.Authorization.Options;

namespace HomeBlaze.Services.Tests.Authorization;

public class RoleExpanderTests
{
    [Fact]
    public void ExpandRoles_AdminIncludesAllRoles()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Admin"] = ["User"],
                ["User"] = ["Guest"],
                ["Guest"] = ["Anonymous"]
            }
        };
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert
        Assert.Contains("Admin", expanded);
        Assert.Contains("User", expanded);
        Assert.Contains("Guest", expanded);
        Assert.Contains("Anonymous", expanded);
    }

    [Fact]
    public void ExpandRoles_GuestIncludesOnlyAnonymous()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Guest"] = ["Anonymous"]
            }
        };
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["Guest"]);

        // Assert
        Assert.Contains("Guest", expanded);
        Assert.Contains("Anonymous", expanded);
        Assert.DoesNotContain("User", expanded);
        Assert.DoesNotContain("Admin", expanded);
    }

    [Fact]
    public void ExpandRoles_UnknownRole_ReturnsOnlyItself()
    {
        // Arrange
        var options = new AuthorizationOptions();
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["CustomRole"]);

        // Assert
        Assert.Single(expanded);
        Assert.Contains("CustomRole", expanded);
    }

    [Fact]
    public void ExpandRoles_MultipleRoles_CombinesHierarchies()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Editor"] = ["Viewer"],
                ["Reviewer"] = ["Viewer"]
            }
        };
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["Editor", "Reviewer"]);

        // Assert
        Assert.Contains("Editor", expanded);
        Assert.Contains("Reviewer", expanded);
        Assert.Contains("Viewer", expanded);
        Assert.Equal(3, expanded.Count); // No duplicates
    }
}
```

**File**: `src/HomeBlaze/HomeBlaze.Services.Tests/Authorization/PermissionResolverTests.cs`

```csharp
using HomeBlaze.Authorization.Services;
using HomeBlaze.Authorization.Options;
using Namotion.Interceptor;
using Moq;

namespace HomeBlaze.Services.Tests.Authorization;

public class PermissionResolverTests
{
    [Fact]
    public void ResolvePropertyRoles_UsesDefaultWhenNoOverride()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultRoles = new Dictionary<string, string[]>
            {
                ["Read"] = ["Anonymous"],
                ["Write"] = ["User"]
            }
        };
        var resolver = new PermissionResolver(options);
        var property = CreateMockProperty("Name");

        // Act
        var readRoles = resolver.ResolvePropertyRoles(property, "Read");
        var writeRoles = resolver.ResolvePropertyRoles(property, "Write");

        // Assert
        Assert.Contains("Anonymous", readRoles);
        Assert.Contains("User", writeRoles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyOverrideTakesPrecedence()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultRoles = new Dictionary<string, string[]>
            {
                ["Read"] = ["Anonymous"]
            }
        };
        var resolver = new PermissionResolver(options);
        var property = CreateMockPropertyWithOverride("SecretCode", "Read", ["Admin"]);

        // Act
        var roles = resolver.ResolvePropertyRoles(property, "Read");

        // Assert
        Assert.Contains("Admin", roles);
        Assert.DoesNotContain("Anonymous", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_SubjectOverrideTakesPrecedence()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultRoles = new Dictionary<string, string[]>
            {
                ["Write"] = ["User"]
            }
        };
        var resolver = new PermissionResolver(options);
        var property = CreateMockPropertyWithSubjectOverride("Name", "Write", ["Admin"]);

        // Act
        var roles = resolver.ResolvePropertyRoles(property, "Write");

        // Assert
        Assert.Contains("Admin", roles);
        Assert.DoesNotContain("User", roles);
    }

    [Fact]
    public void ResolvePropertyRoles_CachesResults()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultRoles = new Dictionary<string, string[]>
            {
                ["Read"] = ["Anonymous"]
            }
        };
        var resolver = new PermissionResolver(options);
        var property = CreateMockProperty("Name");

        // Act
        var roles1 = resolver.ResolvePropertyRoles(property, "Read");
        var roles2 = resolver.ResolvePropertyRoles(property, "Read");

        // Assert - Should return same cached instance
        Assert.Same(roles1, roles2);
    }

    private PropertyReference CreateMockProperty(string name)
    {
        // Create mock property reference for testing
        var mockSubject = new Mock<IInterceptorSubject>();
        mockSubject.Setup(s => s.TryGetPropertyData(It.IsAny<string>(), It.IsAny<string>(), out It.Ref<object?>.IsAny))
            .Returns(false);

        return new PropertyReference(mockSubject.Object, name, null!);
    }

    private PropertyReference CreateMockPropertyWithOverride(string name, string permission, string[] roles)
    {
        var mockSubject = new Mock<IInterceptorSubject>();
        object? rolesObj = roles;
        mockSubject.Setup(s => s.TryGetPropertyData(name, $"{permission}:Roles", out rolesObj))
            .Returns(true);

        return new PropertyReference(mockSubject.Object, name, null!);
    }

    private PropertyReference CreateMockPropertyWithSubjectOverride(string name, string permission, string[] roles)
    {
        var mockSubject = new Mock<IInterceptorSubject>();
        object? rolesObj = roles;
        mockSubject.Setup(s => s.TryGetPropertyData("", $"{permission}:Roles", out rolesObj))
            .Returns(true);

        return new PropertyReference(mockSubject.Object, name, null!);
    }
}
```

**File**: `src/HomeBlaze/HomeBlaze.Services.Tests/Authorization/AuthorizationContextTests.cs`

```csharp
using HomeBlaze.Authorization.Context;
using System.Security.Claims;

namespace HomeBlaze.Services.Tests.Authorization;

public class AuthorizationContextTests
{
    [Fact]
    public void SetUser_StoresUserAndRoles()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "testuser")], "Test"));
        var roles = new HashSet<string> { "Admin", "User" };

        // Act
        AuthorizationContext.SetUser(user, roles);

        // Assert
        Assert.Same(user, AuthorizationContext.CurrentUser);
        Assert.Contains("Admin", AuthorizationContext.ExpandedRoles);
        Assert.Contains("User", AuthorizationContext.ExpandedRoles);
    }

    [Fact]
    public void HasAnyRole_ReturnsTrueWhenUserHasRole()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity([], "Test"));
        AuthorizationContext.SetUser(user, ["Admin", "User"]);

        // Act & Assert
        Assert.True(AuthorizationContext.HasAnyRole(["Admin"]));
        Assert.True(AuthorizationContext.HasAnyRole(["User"]));
        Assert.True(AuthorizationContext.HasAnyRole(["Guest", "Admin"]));
    }

    [Fact]
    public void HasAnyRole_ReturnsFalseWhenUserLacksRole()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity([], "Test"));
        AuthorizationContext.SetUser(user, ["Guest"]);

        // Act & Assert
        Assert.False(AuthorizationContext.HasAnyRole(["Admin"]));
        Assert.False(AuthorizationContext.HasAnyRole(["User", "Admin"]));
    }

    [Fact]
    public async Task AsyncLocal_FlowsAcrossAwait()
    {
        // Arrange
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "asyncuser")], "Test"));
        AuthorizationContext.SetUser(user, ["Admin"]);

        // Act
        ClaimsPrincipal? capturedUser = null;
        await Task.Run(async () =>
        {
            await Task.Delay(10);
            capturedUser = AuthorizationContext.CurrentUser;
        });

        // Assert
        Assert.NotNull(capturedUser);
        Assert.Equal("asyncuser", capturedUser.Identity?.Name);
    }

    [Fact]
    public void AsyncLocal_IsolatesBetweenContexts()
    {
        // Arrange & Act
        var user1Task = Task.Run(() =>
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "user1")], "Test"));
            AuthorizationContext.SetUser(user, ["Role1"]);
            Thread.Sleep(50);
            return AuthorizationContext.CurrentUser?.Identity?.Name;
        });

        var user2Task = Task.Run(() =>
        {
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "user2")], "Test"));
            AuthorizationContext.SetUser(user, ["Role2"]);
            Thread.Sleep(50);
            return AuthorizationContext.CurrentUser?.Identity?.Name;
        });

        Task.WaitAll(user1Task, user2Task);

        // Assert - Each task should see its own user
        Assert.Equal("user1", user1Task.Result);
        Assert.Equal("user2", user2Task.Result);
    }
}
```

---

#### Integration Tests (add to existing `HomeBlaze.Host.Services.Tests`)

**File**: `src/HomeBlaze/HomeBlaze.Host.Services.Tests/Authorization/AuthorizationInterceptorIntegrationTests.cs`

```csharp
using HomeBlaze.Authorization.Context;
using HomeBlaze.Authorization.Interceptors;
using HomeBlaze.Authorization.Services;
using HomeBlaze.Authorization.Options;
using HomeBlaze.Services;
using Microsoft.Extensions.DependencyInjection;
using Namotion.Interceptor;
using System.Security.Claims;

namespace HomeBlaze.Host.Services.Tests.Authorization;

public class AuthorizationInterceptorIntegrationTests
{
    [Fact]
    public void ReadProperty_AllowsAnonymousForPublicProperty()
    {
        // Arrange
        var (context, person) = CreateTestSubject();
        SetAnonymousUser();

        // Act & Assert - Should not throw
        var name = person.Name;
        Assert.NotNull(name);
    }

    [Fact]
    public void WriteProperty_BlocksAnonymousUser()
    {
        // Arrange
        var (context, person) = CreateTestSubject();
        SetAnonymousUser();

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            person.Name = "Hacked";
        });
    }

    [Fact]
    public void WriteProperty_AllowsAuthorizedUser()
    {
        // Arrange
        var (context, person) = CreateTestSubject();
        SetAuthenticatedUser("testuser", ["User"]);

        // Act & Assert - Should not throw
        person.Name = "New Name";
        Assert.Equal("New Name", person.Name);
    }

    [Fact]
    public void ReadProperty_BlocksUnauthorizedForProtectedProperty()
    {
        // Arrange
        var (context, person) = CreateTestSubject();
        SetAuthenticatedUser("testuser", ["Guest"]);

        // Act & Assert
        Assert.Throws<UnauthorizedAccessException>(() =>
        {
            var ssn = person.SocialSecurityNumber;
        });
    }

    [Fact]
    public void ReadProperty_AllowsAdminForProtectedProperty()
    {
        // Arrange
        var (context, person) = CreateTestSubject();
        SetAuthenticatedUser("admin", ["Admin"]);

        // Act & Assert - Should not throw
        var ssn = person.SocialSecurityNumber;
    }

    private (IInterceptorSubjectContext, TestPerson) CreateTestSubject()
    {
        var services = new ServiceCollection();

        var options = new AuthorizationOptions
        {
            UnauthenticatedRole = "Anonymous",
            DefaultRoles = new Dictionary<string, string[]>
            {
                ["Read"] = ["Anonymous"],
                ["Write"] = ["User"]
            },
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Admin"] = ["User"],
                ["User"] = ["Guest"],
                ["Guest"] = ["Anonymous"]
            }
        };

        services.AddSingleton(options);
        services.AddSingleton<IRoleExpander, RoleExpander>();
        services.AddScoped<IPermissionResolver, PermissionResolver>();

        var serviceProvider = services.BuildServiceProvider();

        var context = InterceptorSubjectContext
            .Create()
            .WithFullPropertyTracking()
            .WithService(new AuthorizationInterceptor());

        context.AddService(serviceProvider);

        var person = new TestPerson(context)
        {
            Name = "John Doe",
            SocialSecurityNumber = "123-45-6789"
        };

        return (context, person);
    }

    private void SetAnonymousUser()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        AuthorizationContext.SetUser(user, ["Anonymous"]);
    }

    private void SetAuthenticatedUser(string name, string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));

        // Expand roles
        var expandedRoles = new HashSet<string>(roles);
        // Simulate role expansion (Admin -> User -> Guest -> Anonymous)
        if (roles.Contains("Admin"))
            expandedRoles.UnionWith(["User", "Guest", "Anonymous"]);
        else if (roles.Contains("User"))
            expandedRoles.UnionWith(["Guest", "Anonymous"]);
        else if (roles.Contains("Guest"))
            expandedRoles.Add("Anonymous");

        AuthorizationContext.SetUser(user, expandedRoles);
    }
}

// Test subject for integration tests
[InterceptorSubject]
[DefaultPermissions("Read", "Anonymous")]
[DefaultPermissions("Write", "User")]
public partial class TestPerson
{
    public partial string Name { get; set; }

    [Permission("Admin")]
    public partial string SocialSecurityNumber { get; set; }
}
```

---

**Test Execution**:

```bash
# Run all tests
dotnet test src/Namotion.Interceptor.slnx

# Run only HomeBlaze tests
dotnet test src/HomeBlaze/HomeBlaze.E2E.Tests
dotnet test src/HomeBlaze/HomeBlaze.Services.Tests
dotnet test src/HomeBlaze/HomeBlaze.Host.Services.Tests
```

**Subtasks**:
- [ ] Add AuthorizationTests.cs to HomeBlaze.E2E.Tests
- [ ] Add Authorization folder to HomeBlaze.Services.Tests
- [ ] Add RoleExpanderTests.cs
- [ ] Add PermissionResolverTests.cs
- [ ] Add AuthorizationContextTests.cs
- [ ] Add Authorization folder to HomeBlaze.Host.Services.Tests
- [ ] Add AuthorizationInterceptorIntegrationTests.cs
- [ ] Add TestPerson subject for integration tests

---

## Phase 4: Authentication UI

### Task 13: Create Login Page

**Summary**: Login form with local auth, OAuth buttons, and logout

**Implementation**:
1. Create `Login.razor` page at `/login`
2. Username/password form with validation
3. Remember me checkbox
4. Dynamically show OAuth provider buttons from config
5. Error handling for failed login
6. **Create `/logout` endpoint** (POST, signs out, redirects to home)

**Subtasks**:
- [ ] Tests: Component render tests
- [ ] Docs: N/A
- [ ] Code quality: MudBlazor components
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A
- [ ] Security: Don't reveal if username exists, generic error messages, antiforgery token

---

### Task 14: Create SetPassword Page

**Summary**: First-run password setup for new users

**Implementation**:
1. Create `SetPassword.razor` page
2. Redirect middleware for `MustChangePassword` users
3. Password validation with visual feedback
4. Update user and clear flag
5. **First-run (no current password): Only require new password**
6. **Password change (has password): Require current password**

**Subtasks**:
- [ ] Tests: Password validation, redirect flow
- [ ] Docs: N/A
- [ ] Code quality: Password strength indicator
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A
- [ ] Security: Validate password strength, different flows for first-run vs change

---

### Task 15: Create MyAccount Page

**Summary**: User profile, roles, and linked accounts

**Implementation**:
1. Create `Account.razor` page
2. Profile section (display name, email)
3. Roles display (expanded)
4. Change password dialog (requires current password)
5. Linked OAuth accounts with link/unlink

**Subtasks**:
- [ ] Tests: Profile update tests
- [ ] Docs: N/A
- [ ] Code quality: Confirmation for destructive actions
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A
- [ ] Security: Require re-authentication for sensitive changes

---

### Task 16: Configure App.razor Authentication

**Summary**: Wire up authentication in app root

**Implementation**:
1. Add `<CascadingAuthenticationState>`
2. Configure `<AuthorizeRouteView>`
3. Create `RedirectToLogin` component
4. Create `AccessDenied` component
5. Add login/logout buttons to header layout

**Subtasks**:
- [ ] Tests: Navigation tests
- [ ] Docs: N/A
- [ ] Code quality: N/A
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

## Phase 5: Admin UI

### Task 17: Create User Management Page

**Summary**: CRUD for users with role assignment

**Implementation**:
1. Create `Users.razor` page (Admin only)
2. User table with search/pagination
3. Add user dialog
4. Edit user dialog (display name, roles)
5. Delete with confirmation

**Subtasks**:
- [ ] Tests: CRUD tests
- [ ] Docs: N/A
- [ ] Code quality: Pagination, search debouncing
- [ ] Dependencies: No new dependencies
- [ ] Performance: Server-side pagination
- [ ] Security: Prevent self-deletion, prevent removal of last admin

---

### Task 18: Create Role Configuration Page

**Summary**: Manage role composition and external mappings (stored in database)

**Implementation**:
1. Create `Roles.razor` page (Admin only)
2. Role table with includes display
3. Add/edit role dialog with composition picker
4. External role mappings CRUD
5. Circular reference validation
6. **Call `IRoleExpander.ReloadAsync()` on changes** (claims transformation uses this)

**Subtasks**:
- [ ] Tests: Role CRUD, validation tests
- [ ] Docs: N/A
- [ ] Code quality: Real-time validation
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A
- [ ] Security: Protect system roles (Anonymous, Guest, User, Admin) from deletion

---

### Task 19: Create Subject Authorization Panel

**Summary**: Inline authorization editor for subjects

**Implementation**:
1. Create `SubjectAuthorizationPanel.razor` component
2. Permission table with source indicators (ExtData/Attribute/Inherited/Default)
3. Property/method override tables
4. Edit role mapping dialog (sets ExtData)
5. Clear override action

**Subtasks**:
- [ ] Tests: Component tests
- [ ] Docs: N/A
- [ ] Code quality: Consistent with existing subject editors
- [ ] Dependencies: No new dependencies
- [ ] Performance: Lazy load property/method lists

---

## Phase 6: Filtering & Feedback

### Task 20: Implement Navigation Filtering

**Summary**: Hide unauthorized menu items

**Implementation**:
1. Update `NavMenu.razor` with `<AuthorizeView>`
2. Filter dynamic navigation items by role

**Subtasks**:
- [ ] Tests: Menu visibility tests
- [ ] Docs: N/A
- [ ] Code quality: N/A
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

### Task 21: Implement Subject Browser Filtering

**Summary**: Filter visible subjects by Read access

**Implementation**:
1. Create `AuthorizedSubjectBrowserService`
2. Filter subjects in existing browser component
3. Integrate with resolver

**Subtasks**:
- [ ] Tests: Filtering tests
- [ ] Docs: N/A
- [ ] Code quality: N/A
- [ ] Dependencies: No new dependencies
- [ ] Performance: Cache accessible subjects per user, hierarchical filtering

---

### Task 22: Implement Property/Method Access Filtering

**Summary**: Hide unauthorized properties, show read-only for read-but-not-write

**Implementation**:
1. Update property editors with access checks
2. Update method buttons with invoke checks
3. Add read-only styling
4. Disable controls for readable-but-not-writable

**Subtasks**:
- [ ] Tests: UI access tests
- [ ] Docs: N/A
- [ ] Code quality: Consistent styling
- [ ] Dependencies: No new dependencies
- [ ] Performance: Cache access checks per render

---

## Phase 7: Application Integration

### Task 23: Create ServiceCollectionExtensions

**Summary**: Main DI registration extension method

**Implementation**:
1. Create `AddHomeBlazeAuthorization()` that calls:
   - `AddHomeBlazeIdentity()`
   - `AddHomeBlazeOAuth()` (if configured)
   - Register `IPermissionResolver` as **scoped** (for per-request caching)
   - Register `AuthorizedMethodInvoker` as decorator
   - Register `HomeBlazeAuthenticationStateProvider` (extends `RevalidatingServerAuthenticationStateProvider`)
2. Create middleware registration extension (`UseAuthorizationContext`)
3. Create `WithAuthorization()` extension method for context (follows codebase pattern)

```csharp
public static IServiceCollection AddHomeBlazeAuthorization(
    this IServiceCollection services,
    Action<AuthorizationOptions>? configure = null)
{
    var options = new AuthorizationOptions();
    configure?.Invoke(options);

    services.AddSingleton(options);
    services.AddSingleton<IRoleExpander, RoleExpander>();
    services.AddScoped<IPermissionResolver, PermissionResolver>();

    // Use RevalidatingServerAuthenticationStateProvider for Blazor Server
    services.AddScoped<AuthenticationStateProvider, HomeBlazeAuthenticationStateProvider>();

    return services;
}

public static IApplicationBuilder UseAuthorizationContext(this IApplicationBuilder app)
{
    return app.UseMiddleware<AuthorizationContextMiddleware>();
}
```

**Note**: `AuthorizationInterceptor` is registered directly on context via extension method, `IPermissionResolver` is resolved via DI

**CRITICAL**: IServiceProvider must be explicitly registered on the context for DI resolution to work:
```csharp
context.AddService(serviceProvider); // Required for TryGetService<IServiceProvider>() to work
```

**Subtasks**:
- [ ] Tests: Registration tests
- [ ] Docs: Usage documentation
- [ ] Code quality: Fluent API
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

### Task 24: Update SubjectContextFactory

**Summary**: Add authorization interceptor to subject context

**Implementation**:
1. Update `SubjectContextFactory.Create()` to use `.WithAuthorization()` extension
2. **CRITICAL**: Register `IServiceProvider` on context for DI resolution
3. Interceptor uses `[RunsFirst]` so ordering is automatic

```csharp
// Extension method (in HomeBlaze.Authorization)
public static class AuthorizationContextExtensions
{
    public static IInterceptorSubjectContext WithAuthorization(this IInterceptorSubjectContext context)
    {
        return context.WithService(() => new AuthorizationInterceptor());
    }
}

// SubjectContextFactory.cs
public static IInterceptorSubjectContext Create(IServiceProvider serviceProvider)
{
    var context = InterceptorSubjectContext
        .Create()
        .WithFullPropertyTracking()
        .WithReadPropertyRecorder()
        .WithRegistry()
        .WithParents()
        .WithLifecycle()
        .WithAuthorization() // Uses extension method
        .WithDataAnnotationValidation()
        .WithHostedServices(services);

    // CRITICAL: Register IServiceProvider for DI resolution in interceptors
    context.AddService(serviceProvider);

    return context;
}
```

**Subtasks**:
- [ ] Tests: Integration tests
- [ ] Docs: N/A
- [ ] Code quality: Proper ordering
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

### Task 25: Update Program.cs

**Summary**: Configure middleware and services in HomeBlaze.Host

**Implementation**:
1. Add `builder.Services.AddHomeBlazeAuthorization()`
2. Add `app.UseAuthentication()`
3. Add `app.UseAuthorization()`
4. Add authorization context middleware (populates AsyncLocal)
5. Ensure proper middleware ordering

**Subtasks**:
- [ ] Tests: Startup tests
- [ ] Docs: N/A
- [ ] Code quality: N/A
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A

---

## Phase 8: Session Management

### Task 26: Implement RevalidatingAuthStateProvider

**Summary**: Periodic role revalidation for connected users

**Implementation**:
1. Create `RevalidatingAuthenticationStateProvider`
2. 1-minute revalidation interval
3. Check for role changes, user deletion
4. Force re-auth on changes

**Subtasks**:
- [ ] Tests: Revalidation tests
- [ ] Docs: Document session behavior
- [ ] Code quality: Proper async disposal
- [ ] Dependencies: No new dependencies
- [ ] Performance: Lightweight revalidation query

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1. Infrastructure | 1, 1b, 1c, 2 | Projects, ISubjectDataProvider, serializer update, options |
| 2. Identity & DB | 3-7 | DbContext, role expander, claims transform, seeding, Identity, OAuth |
| 3. Authorization | 8, 8b, 9-12 | Attributes, PermissionsDataProvider, resolver, interceptor, method auth, user context |
| 4. Auth UI | 13-16 | Login/logout, SetPassword, Account, App.razor |
| 5. Admin UI | 17-19 | Users, Roles, Subject panel |
| 6. Filtering | 20-22 | Nav, browser, properties |
| 7. Integration | 23-25 | DI, SubjectContextFactory, Program.cs |
| 8. Session | 26 | Revalidation |

**Total: 30 tasks across 8 phases** (includes 1b, 1c for serialization extensibility, 4b for claims, 8b for persistence, 12b for tests)

---

## Dependencies Summary

**New NuGet Packages**:
- `Microsoft.AspNetCore.Identity.EntityFrameworkCore`
- `Microsoft.EntityFrameworkCore.Sqlite`
- `Microsoft.AspNetCore.Authentication.OpenIdConnect`

**Internal Dependencies**:
- `Namotion.Interceptor.Tracking` (for `GetParents()`)
- `HomeBlaze.Abstractions` (for `[Permission]` attribute)
- `HomeBlaze.Host` (for existing UI components)

---

## Performance Optimizations Summary

| Component | Optimization |
|-----------|--------------|
| Role expansion | `IRoleExpander` caches composition, returns `ImmutableHashSet` |
| Claims transformation | Uses cached `IRoleExpander`, runs once per request |
| Permission resolution | Cache per (subject, property, permission) in ConcurrentDictionary |
| Attribute lookup | FrozenDictionary per Type at startup |
| Parent traversal | Pooled HashSet, track visited |
| User role checks | `ImmutableHashSet` for O(1) Contains |
| Method permissions | Cache per (Type, methodName) at startup |
| Subject browser | Cache accessible subjects per user |
