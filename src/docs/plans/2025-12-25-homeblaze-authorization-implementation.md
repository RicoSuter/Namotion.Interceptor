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
| Password length | Custom | 6 characters minimum |
| Password complexity | Custom | None (no requirements) |
| Lockout attempts | Identity default | 5 attempts |
| Lockout duration | Identity default | 5 minutes |
| Cookie expiration | Identity default | 14 days, sliding |
| Concurrent sessions | Identity default | Allowed |
| HTTPS | ASP.NET Core | `UseHttpsRedirection()` - only redirects if HTTPS configured |

**No open questions - all resolved with framework defaults or simple custom settings.**

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

**Location**: `HomeBlaze.Abstractions` (to avoid circular dependencies - HomeBlaze.Authorization can reference it)

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
       /// Keys have "$" prefix stripped (e.g., "$authorization" → "authorization").
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

**Breaking Change**: This modifies the `ConfigurableSubjectSerializer` constructor signature:
- **Before**: `ConfigurableSubjectSerializer(TypeProvider, IServiceProvider)`
- **After**: `ConfigurableSubjectSerializer(TypeProvider, IServiceProvider, IEnumerable<ISubjectDataProvider>)`

**Migration**:
1. Update all tests that instantiate `ConfigurableSubjectSerializer` directly
2. DI registration automatically resolves `IEnumerable<ISubjectDataProvider>` (empty if none registered)
3. Existing code using DI works without changes

**Test Updates Required**:
```csharp
// Before
var serializer = new ConfigurableSubjectSerializer(typeProvider, serviceProvider);

// After
var serializer = new ConfigurableSubjectSerializer(
    typeProvider,
    serviceProvider,
    Enumerable.Empty<ISubjectDataProvider>());  // or: []
```

**Subtasks**:
- [ ] Tests: Serialization round-trip tests with provider
- [ ] Tests: Update existing ConfigurableSubjectSerializer tests
- [ ] Docs: N/A
- [ ] Code quality: Backwards compatible via DI
- [ ] Dependencies: No new dependencies
- [ ] Performance: Provider iteration is O(n) where n = number of providers

---

### Task 1d: ISubjectDataProvider Unit Tests

**Summary**: Comprehensive tests for extensible serialization

**Location**: `HomeBlaze.Services.Tests`

**Test Cases**:
1. **Round-trip serialization**:
   - Serialize subject with `$authorization` data
   - Deserialize and verify data restored to Subject.Data
   - Verify `[Configuration]` properties still work

2. **Empty provider scenario**:
   - No `ISubjectDataProvider` registered
   - Verify serialization works without `$` properties

3. **Multiple providers**:
   - Two providers returning different `$` properties
   - Verify both are serialized

4. **Edge cases**:
   - Null subject passed to provider
   - Empty permissions dictionary
   - Property name with special characters

5. **Backward compatibility**:
   - Deserialize JSON without `$` properties (old format)
   - Verify no errors, subject created normally

```csharp
public class SubjectDataProviderTests
{
    [Fact]
    public void Serialize_WithPermissionsProvider_Includes$PermissionsProperty()
    {
        // Arrange
        var subject = CreateTestSubject();
        subject.Data[(null, "HomeBlaze.Authorization:Read")] = new[] { "Admin" };

        var serializer = CreateSerializer(new AuthorizationDataProvider());

        // Act
        var json = serializer.Serialize(subject);

        // Assert
        json.Should().Contain("\"$authorization\"");
        json.Should().Contain("\"Read\":[\"Admin\"]");
    }

    [Fact]
    public void Deserialize_With$Permissions_RestoresSubjectData()
    {
        // Arrange
        var json = """
        {
            "$type": "TestSubject",
            "name": "Test",
            "$authorization": {
                "": { "State:Read": ["Guest"], "State:Write": ["Admin"] }
            }
        }
        """;

        var serializer = CreateSerializer(new AuthorizationDataProvider());

        // Act
        var subject = serializer.Deserialize(json);

        // Assert
        subject.Data[(null, "HomeBlaze.Authorization:State:Read")].Should().BeEquivalentTo(new[] { "Guest" });
        subject.Data[(null, "HomeBlaze.Authorization:State:Write")].Should().BeEquivalentTo(new[] { "Admin" });
    }
}
```

**Subtasks**:
- [ ] Create test fixture with mock subjects
- [ ] Test provider registration in DI
- [ ] Test with real ConfigurableSubjectSerializer
- [ ] Performance: N/A

---

### Task 2: Implement AuthorizationOptions

**Summary**: Configuration class for authorization settings (use Identity defaults for password/session)

**Implementation**:
1. Create `AuthorizationOptions.cs`:
   ```csharp
   public class AuthorizationOptions
   {
       public string UnauthenticatedRole { get; set; } = DefaultRoles.Anonymous;
       public Dictionary<(Kind, AuthorizationAction), string[]> DefaultPermissionRoles { get; set; } = new()
       {
           [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Guest],
           [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Operator],
           [(Kind.Configuration, AuthorizationAction.Read)] = [DefaultRoles.User],
           [(Kind.Configuration, AuthorizationAction.Write)] = [DefaultRoles.Supervisor],
           [(Kind.Query, AuthorizationAction.Invoke)] = [DefaultRoles.User],
           [(Kind.Operation, AuthorizationAction.Invoke)] = [DefaultRoles.Operator]
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
2. Seed default roles in AspNetRoles + RoleComposition (stored in database, editable via Admin UI):
   - Anonymous = []
   - Guest = [Anonymous]
   - User = [Guest]
   - Operator = [User]
   - Supervisor = [Operator]
   - Admin = [Supervisor]
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

### Task 8: Create Authorization Enums, Attributes, and Roles

**Summary**: Type-safe authorization model with Kind + Action dimensions

**Implementation**:

1. Create `AuthorizationAction` enum:
   ```csharp
   public enum AuthorizationAction
   {
       Read,    // Read property value
       Write,   // Write property value
       Invoke   // Invoke method
   }
   ```

2. Create `Kind` enum:
   ```csharp
   public enum Kind
   {
       State,          // Runtime state properties
       Configuration,  // Persisted configuration properties
       Query,          // Read-only methods (idempotent)
       Operation       // Mutating methods (side effects)
   }
   ```

3. Create `DefaultRoles` static class:
   ```csharp
   public static class DefaultRoles
   {
       public const string Anonymous = "Anonymous";
       public const string Guest = "Guest";
       public const string User = "User";
       public const string Operator = "Operator";
       public const string Supervisor = "Supervisor";
       public const string Admin = "Admin";
   }
   ```

4. Create `[SubjectAuthorize]` attribute (on class):
   ```csharp
   [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
   public class SubjectAuthorizeAttribute : Attribute
   {
       public Kind Kind { get; }
       public Action Action { get; }
       public string[] Roles { get; }

       public SubjectAuthorizeAttribute(Kind kind, AuthorizationAction action, params string[] roles)
       {
           Kind = kind;
           Action = action;
           Roles = roles;
       }
   }
   ```

5. Create `[SubjectPropertyAuthorize]` attribute (on property):
   ```csharp
   [AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
   public class SubjectPropertyAuthorizeAttribute : Attribute
   {
       public Action Action { get; }
       public string[] Roles { get; }

       public SubjectPropertyAuthorizeAttribute(AuthorizationAction action, params string[] roles)
       {
           Action = action;
           Roles = roles;
       }
   }
   ```

6. Create `[SubjectMethodAuthorize]` attribute (on method):
   ```csharp
   [AttributeUsage(AttributeTargets.Method)]
   public class SubjectMethodAuthorizeAttribute : Attribute
   {
       public string[] Roles { get; }

       public SubjectMethodAuthorizeAttribute(params string[] roles)
       {
           Roles = roles;
       }
   }
   ```

**Note**: All attributes should live in `HomeBlaze.Abstractions` to avoid circular dependencies.

**Subtasks**:
- [ ] Tests: Attribute parsing via reflection
- [ ] Docs: Usage examples in XML comments
- [ ] Code quality: Allow multiple on same target
- [ ] Dependencies: No new dependencies
- [ ] Performance: N/A (reflection cached in resolver)

---

### Task 8b: Implement AuthorizationDataProvider

**Summary**: Serialization provider for authorization override persistence

**Location**: `HomeBlaze.Authorization`

**Implementation**:
1. Create `AuthorizationDataProvider : ISubjectDataProvider`
2. Use `HomeBlaze.Authorization:Kind:Action` format for Subject.Data keys
3. Serialize overrides to `$authorization` JSON property
4. **Handle non-configurable descendants**: Collect permissions from descendants that don't implement `IConfigurableSubject` and store them with path prefix
5. Register as `ISubjectDataProvider` in DI

**Storage key format**: `HomeBlaze.Authorization:{Kind}:{Action}` e.g.:
- `HomeBlaze.Authorization:State:Read`
- `HomeBlaze.Authorization:Configuration:Write`

**Descendant Storage**:
Only `IConfigurableSubject` subjects are serialized. Permission overrides on non-configurable descendants must be persisted with the nearest configurable ancestor.

- **Runtime**: Overrides stored in each subject's own `Data` dictionary (works normally)
- **Serialize**: Walk descendants, collect permissions from non-`IConfigurableSubject` children, store with path
- **Deserialize**: Use path resolver to find target subject, restore to correct `Data` dictionary

**Path format** (uses existing path resolver patterns):
- `""` in JSON = subject-level on this configurable subject (maps to `null` in Subject.Data)
- `PropertyName` = property on this subject
- `Child/PropertyName` = property on child subject
- `Children[2]/PropertyName` = property on indexed child

**Note**: JSON uses `""` (empty string) for subject-level since JSON doesn't support null keys. In `Subject.Data`, use `null` for subject-level per Namotion.Interceptor convention.

All non-empty paths resolve to properties. No subject-level overrides on descendants - permissions flow through property access.

```csharp
public class AuthorizationOverride
{
    public bool Inherit { get; set; }
    public string[] Roles { get; set; } = [];
}

public class AuthorizationDataProvider : ISubjectDataProvider
{
    private const string DataKeyPrefix = "HomeBlaze.Authorization:";
    private const string PropertyKey = "authorization";
    private readonly ISubjectPathResolver _pathResolver;

    public AuthorizationDataProvider(ISubjectPathResolver pathResolver)
    {
        _pathResolver = pathResolver;
    }

    public Dictionary<string, object>? GetAdditionalProperties(IInterceptorSubject subject)
    {
        if (subject?.Data == null)
            return null;

        // Structure: { "path": { "Kind:Action": { inherit, roles } } }
        var permissions = new Dictionary<string, Dictionary<string, AuthorizationOverride>>();

        // 1. Collect permissions from this subject
        CollectSubjectPermissions(subject, "", permissions);

        // 2. Walk descendants and collect from non-IConfigurableSubject children
        CollectDescendantPermissions(subject, "", permissions);

        return permissions.Count > 0
            ? new Dictionary<string, object> { [PropertyKey] = permissions }
            : null;
    }

    private void CollectSubjectPermissions(
        IInterceptorSubject subject,
        string pathPrefix,
        Dictionary<string, Dictionary<string, AuthorizationOverride>> result)
    {
        foreach (var kvp in subject.Data)
        {
            if (!kvp.Key.key.StartsWith(DataKeyPrefix) || kvp.Value is not AuthorizationOverride authOverride)
                continue;

            var kindAction = kvp.Key.key[DataKeyPrefix.Length..];
            // Subject.Data uses null for subject-level, convert to "" for JSON
            var propertyName = kvp.Key.property ?? "";

            // Skip subject-level on descendants - not supported
            if (string.IsNullOrEmpty(propertyName) && !string.IsNullOrEmpty(pathPrefix))
                continue;

            var fullPath = string.IsNullOrEmpty(pathPrefix)
                ? propertyName
                : $"{pathPrefix}/{propertyName}";

            if (!result.TryGetValue(fullPath, out var permDict))
            {
                permDict = [];
                result[fullPath] = permDict;
            }
            permDict[kindAction] = authOverride;
        }
    }

    private void CollectDescendantPermissions(
        IInterceptorSubject parent,
        string pathPrefix,
        Dictionary<string, Dictionary<string, AuthorizationOverride>> result)
    {
        foreach (var prop in parent.Properties.Values)
        {
            var value = prop.PropertyInfo?.GetValue(parent);

            // Handle single child subject
            if (value is IInterceptorSubject child && child is not IConfigurableSubject)
            {
                var childPath = string.IsNullOrEmpty(pathPrefix)
                    ? prop.Name
                    : $"{pathPrefix}/{prop.Name}";
                CollectSubjectPermissions(child, childPath, result);
                CollectDescendantPermissions(child, childPath, result);
            }

            // Handle collection of subjects
            if (value is IEnumerable<IInterceptorSubject> children)
            {
                var index = 0;
                foreach (var item in children)
                {
                    if (item is not IConfigurableSubject)
                    {
                        var itemPath = string.IsNullOrEmpty(pathPrefix)
                            ? $"{prop.Name}[{index}]"
                            : $"{pathPrefix}/{prop.Name}[{index}]";
                        CollectSubjectPermissions(item, itemPath, result);
                        CollectDescendantPermissions(item, itemPath, result);
                    }
                    index++;
                }
            }
        }
    }

    public void SetAdditionalProperties(
        IInterceptorSubject subject,
        Dictionary<string, JsonElement> properties)
    {
        if (subject == null || properties == null)
            return;

        if (!properties.TryGetValue(PropertyKey, out var element))
            return;

        var overrides = element.Deserialize<
            Dictionary<string, Dictionary<string, AuthorizationOverride>>>();
        if (overrides == null)
            return;

        foreach (var (path, overrideMap) in overrides)
        {
            if (overrideMap == null)
                continue;

            // Resolve path to target subject and property
            var (targetSubject, propertyName) = ResolvePath(subject, path);
            if (targetSubject == null)
                continue;

            foreach (var (kindAction, authOverride) in overrideMap)
            {
                if (authOverride != null)
                {
                    targetSubject.Data[(propertyName, $"{DataKeyPrefix}{kindAction}")] = authOverride;
                }
            }
        }
    }

    private (IInterceptorSubject? subject, string? propertyName) ResolvePath(
        IInterceptorSubject root,
        string path)
    {
        // "" in JSON = subject-level on root (maps to null in Subject.Data)
        if (string.IsNullOrEmpty(path))
            return (root, null);

        // Use path resolver to get the property reference
        var propertyRef = _pathResolver.ResolveProperty(root, path);
        if (propertyRef == null)
            return (null, null);

        return (propertyRef.Value.Subject, propertyRef.Value.Name);
    }
}
```

**Authorization Override Schema**:
```csharp
public class AuthorizationOverride
{
    public bool Inherit { get; set; }  // true = extend, false = replace
    public string[] Roles { get; set; } = [];
}
```

- `inherit: false` → Replace: Only these roles can access (ignores parent/defaults)
- `inherit: true` → Extend: These roles can also access, in addition to inherited roles

**JSON Output**: See [Design Document Section 26](./2025-12-25-homeblaze-authorization-design.md#26-authorization-data-persistence) for complete JSON schema examples.

**Registration**:
```csharp
// In AddHomeBlazeAuthorization()
services.AddSingleton<ISubjectDataProvider, AuthorizationDataProvider>();
```

**Subtasks**:
- [ ] Tests: Round-trip serialization tests (flat format)
- [ ] Tests: Round-trip serialization with descendant overrides
- [ ] Tests: Path resolution with missing/changed descendants (graceful skip)
- [ ] Docs: N/A
- [ ] Code quality: Use constants for key prefix
- [ ] Dependencies: Depends on Task 1b (ISubjectDataProvider)
- [ ] Performance: Cache property navigation if needed

---

### Task 8c: Role Hierarchy Edge Case Tests

**Summary**: Tests for role composition edge cases and circular reference detection

**Location**: `HomeBlaze.Authorization.Tests`

**Test Cases**:
1. **Circular role detection**:
   - A → B → C → A (throws with clear message)
   - Self-reference A → A (ignored, no error)

2. **Deep hierarchy**:
   - Chain of 10+ roles
   - Verify all are expanded correctly

3. **Diamond inheritance**:
   - Admin → [User, SecurityGuard]
   - User → Guest
   - SecurityGuard → Guest
   - Verify Guest only appears once in expanded set

4. **Unknown role handling**:
   - Role not in hierarchy config
   - Should be passed through unchanged

5. **Empty hierarchy**:
   - No role composition configured
   - Each role stands alone

```csharp
public class RoleExpanderEdgeCaseTests
{
    [Fact]
    public void ExpandRoles_CircularReference_ThrowsWithClearMessage()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["A"] = ["B"],
                ["B"] = ["C"],
                ["C"] = ["A"]  // Circular!
            }
        };
        var expander = new RoleExpander(options);

        // Act & Assert
        var ex = Assert.Throws<InvalidOperationException>(() =>
            expander.ExpandRoles(["A"]));

        ex.Message.Should().Contain("circular");
        ex.Message.Should().ContainAny("A", "B", "C");
    }

    [Fact]
    public void ExpandRoles_SelfReference_IsIgnored()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Admin"] = ["Admin", "User"]  // Self-reference
            }
        };
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert - Should not throw, Admin appears once
        expanded.Should().Contain("Admin");
        expanded.Should().Contain("User");
        expanded.Count(r => r == "Admin").Should().Be(1);
    }

    [Fact]
    public void ExpandRoles_DiamondInheritance_NoDuplicates()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            RoleHierarchy = new Dictionary<string, string[]>
            {
                ["Admin"] = ["User", "SecurityGuard"],
                ["User"] = ["Guest"],
                ["SecurityGuard"] = ["Guest"]
            }
        };
        var expander = new RoleExpander(options);

        // Act
        var expanded = expander.ExpandRoles(["Admin"]);

        // Assert - Guest should appear only once
        expanded.Should().Contain("Guest");
        expanded.Count(r => r == "Guest").Should().Be(1);
    }
}
```

**Subtasks**:
- [ ] Test circular detection at startup vs first use
- [ ] Performance test with large hierarchies
- [ ] Integration with claims transformation

---

### Task 9: Implement AuthorizationResolver

**Summary**: Core resolution logic with Kind+Action dimensions, inheritance traversal and caching

**Implementation**:
1. Create `IAuthorizationResolver` interface with Kind+Action parameters
2. Implement 6-level priority chain:
   - Property runtime override (Kind, AuthorizationAction)
   - Subject runtime override (Kind, AuthorizationAction)
   - Property [SubjectPropertyAuthorize] attribute
   - Subject [SubjectAuthorize] attribute
   - Parent traversal (OR combine)
   - Global defaults (Kind, AuthorizationAction)
3. Implement `TraverseParents()` with OR combination and visited set
4. **Cache resolved permissions per (subject, property, kind, action)** - critical for performance

```csharp
public interface IAuthorizationResolver
{
    string[] ResolvePropertyRoles(PropertyReference property, Kind kind, AuthorizationAction action);
    string[] ResolveSubjectRoles(IInterceptorSubject subject, Kind kind, AuthorizationAction action);
    string[] ResolveMethodRoles(IInterceptorSubject subject, string methodName, Kind kind);
    void InvalidateCache(IInterceptorSubject subject); // Call on ExtData change
}
```

**Resolution for properties**:
```csharp
public string[] ResolvePropertyRoles(PropertyReference property, Kind kind, AuthorizationAction action)
{
    // 1. Property runtime override
    var key = $"HomeBlaze.Authorization:{kind}:{action}";
    if (property.TryGetPropertyData(key, out var roles))
        return (string[])roles;

    // 2. Subject runtime override
    if (property.Subject.Data.TryGetValue(("", key), out var subjectRoles))
        return (string[])subjectRoles;

    // 3. Property [SubjectPropertyAuthorize] attribute
    var propAttr = GetPropertyAttribute(property, action);
    if (propAttr != null) return propAttr.Roles;

    // 4. Subject [SubjectAuthorize] attribute
    var subjectAttr = GetSubjectAttribute(property.Subject, kind, action);
    if (subjectAttr != null) return subjectAttr.Roles;

    // 5. Parent traversal
    var parentRoles = TraverseParents(property.Subject, kind, action);
    if (parentRoles.Length > 0) return parentRoles;

    // 6. Global defaults
    return _options.Defaults[(kind, action)];
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
  - **Cache per (subject, propertyName, kind, action) using ConcurrentDictionary**
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
   - Read: Resolve Kind from property's marker ([State]/[Configuration]), check Read action
   - Write: Resolve Kind from property's marker, check Write action
   - Throw `UnauthorizedAccessException` on denied access (defense in depth)
   - UI should filter - interceptor is last line of defense
2. Resolve `IAuthorizationResolver` via DI (`IServiceProvider` from context)
3. Access user context via `AsyncLocal<ClaimsPrincipal>` (populated at request start)

```csharp
[RunsFirst] // Ensure auth check happens before any other interceptor
public class AuthorizationInterceptor : IReadInterceptor, IWriteInterceptor
{
    public TProperty ReadProperty<TProperty>(ref PropertyReadContext context, ...)
    {
        // Resolve from DI via IServiceProvider
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IAuthorizationResolver>();
        if (resolver == null) return next(ref context); // Graceful degradation

        // Get Kind from property's marker attribute ([State] or [Configuration])
        var kind = GetPropertyKind(context.Property); // State or Configuration
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Read);
        var user = AuthorizationContext.CurrentUser; // AsyncLocal

        if (user != null && !HasAnyRole(user, requiredRoles))
            throw new UnauthorizedAccessException($"Access denied: {kind}+Read requires [{string.Join(", ", requiredRoles)}]");

        return next(ref context);
    }

    public void WriteProperty<TProperty>(ref PropertyWriteContext context, ...)
    {
        var serviceProvider = context.Property.Subject.Context.TryGetService<IServiceProvider>();
        var resolver = serviceProvider?.GetService<IAuthorizationResolver>();
        if (resolver == null) { next(ref context); return; }

        var kind = GetPropertyKind(context.Property);
        var requiredRoles = resolver.ResolvePropertyRoles(context.Property, kind, AuthorizationAction.Write);
        var user = AuthorizationContext.CurrentUser;

        if (user != null && !HasAnyRole(user, requiredRoles))
            throw new UnauthorizedAccessException($"Access denied: {kind}+Write requires [{string.Join(", ", requiredRoles)}]");

        next(ref context);
    }

    private Kind GetPropertyKind(PropertyReference property)
    {
        // Check for [State] or [Configuration] attribute on property
        // Default to State if no marker
        return property.Metadata.HasAttribute<ConfigurationAttribute>()
            ? Kind.Configuration
            : Kind.State;
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

**Summary**: Decorator for ISubjectMethodInvoker that checks Invoke permission with Kind

**Implementation**:
1. Create `AuthorizedMethodInvoker : ISubjectMethodInvoker`
2. Get Kind from method's marker ([Query] or [Operation])
3. Wrap existing invoker, check permission before delegating
4. Use same resolution logic as property authorization

```csharp
public class AuthorizedMethodInvoker : ISubjectMethodInvoker
{
    private readonly ISubjectMethodInvoker _inner;
    private readonly IAuthorizationResolver _resolver;

    public async Task<MethodInvocationResult> InvokeAsync(...)
    {
        var kind = GetMethodKind(method); // Query or Operation
        var requiredRoles = _resolver.ResolveMethodRoles(subject, method.Name, kind);
        // Check, then delegate to _inner
    }

    private Kind GetMethodKind(MethodInfo method)
    {
        return method.GetCustomAttribute<QueryAttribute>() != null
            ? Kind.Query
            : Kind.Operation; // Default to Operation
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

            try
            {
                // Update authorization context with current user
                await UpdateAuthorizationContextAsync();

                // Execute the activity (event handler, render, etc.)
                await next(context);
            }
            finally
            {
                // Clear services accessor to prevent stale state on exceptions
                _servicesAccessor.Services = null;
            }
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

**File**: `src/HomeBlaze/HomeBlaze.Services.Tests/Authorization/AuthorizationResolverTests.cs`

```csharp
using HomeBlaze.Authorization.Services;
using HomeBlaze.Authorization.Options;
using Namotion.Interceptor;
using Moq;

namespace HomeBlaze.Services.Tests.Authorization;

public class AuthorizationResolverTests
{
    [Fact]
    public void ResolvePropertyRoles_UsesDefaultWhenNoOverride()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
            {
                [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Guest],
                [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Operator]
            }
        };
        var resolver = new AuthorizationResolver(options);
        var property = CreateMockProperty("Name");

        // Act
        var readRoles = resolver.ResolvePropertyRoles(property, Kind.State, AuthorizationAction.Read);
        var writeRoles = resolver.ResolvePropertyRoles(property, Kind.State, AuthorizationAction.Write);

        // Assert
        Assert.Contains(DefaultRoles.Guest, readRoles);
        Assert.Contains(DefaultRoles.Operator, writeRoles);
    }

    [Fact]
    public void ResolvePropertyRoles_PropertyOverrideTakesPrecedence()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
            {
                [(Kind.Configuration, AuthorizationAction.Read)] = [DefaultRoles.User]
            }
        };
        var resolver = new AuthorizationResolver(options);
        var property = CreateMockPropertyWithOverride("SecretCode", Kind.Configuration, AuthorizationAction.Read, [DefaultRoles.Admin]);

        // Act
        var roles = resolver.ResolvePropertyRoles(property, Kind.Configuration, AuthorizationAction.Read);

        // Assert
        Assert.Contains(DefaultRoles.Admin, roles);
        Assert.DoesNotContain(DefaultRoles.User, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_SubjectOverrideTakesPrecedence()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
            {
                [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Operator]
            }
        };
        var resolver = new AuthorizationResolver(options);
        var property = CreateMockPropertyWithSubjectOverride("Name", Kind.State, AuthorizationAction.Write, [DefaultRoles.Admin]);

        // Act
        var roles = resolver.ResolvePropertyRoles(property, Kind.State, AuthorizationAction.Write);

        // Assert
        Assert.Contains(DefaultRoles.Admin, roles);
        Assert.DoesNotContain(DefaultRoles.Operator, roles);
    }

    [Fact]
    public void ResolvePropertyRoles_CachesResults()
    {
        // Arrange
        var options = new AuthorizationOptions
        {
            DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
            {
                [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Guest]
            }
        };
        var resolver = new AuthorizationResolver(options);
        var property = CreateMockProperty("Name");

        // Act
        var roles1 = resolver.ResolvePropertyRoles(property, Kind.State, AuthorizationAction.Read);
        var roles2 = resolver.ResolvePropertyRoles(property, Kind.State, AuthorizationAction.Read);

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
            UnauthenticatedRole = DefaultRoles.Anonymous,
            DefaultPermissionRoles = new Dictionary<(Kind, AuthorizationAction), string[]>
            {
                [(Kind.State, AuthorizationAction.Read)] = [DefaultRoles.Guest],
                [(Kind.State, AuthorizationAction.Write)] = [DefaultRoles.Operator],
                [(Kind.Configuration, AuthorizationAction.Read)] = [DefaultRoles.User],
                [(Kind.Configuration, AuthorizationAction.Write)] = [DefaultRoles.Supervisor]
            },
            RoleHierarchy = new Dictionary<string, string[]>
            {
                [DefaultRoles.Admin] = [DefaultRoles.Supervisor],
                [DefaultRoles.Supervisor] = [DefaultRoles.Operator],
                [DefaultRoles.Operator] = [DefaultRoles.User],
                [DefaultRoles.User] = [DefaultRoles.Guest]
            }
        };

        services.AddSingleton(options);
        services.AddSingleton<IRoleExpander, RoleExpander>();
        services.AddScoped<IAuthorizationResolver, AuthorizationResolver>();

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
[SubjectAuthorize(Kind.State, AuthorizationAction.Read, DefaultRoles.Guest)]
[SubjectAuthorize(Kind.State, AuthorizationAction.Write, DefaultRoles.Operator)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Read, DefaultRoles.User)]
[SubjectAuthorize(Kind.Configuration, AuthorizationAction.Write, DefaultRoles.Supervisor)]
public partial class TestPerson
{
    [State]
    public partial string Name { get; set; }

    [Configuration]
    [SubjectPropertyAuthorize(AuthorizationAction.Read, DefaultRoles.Admin)]
    [SubjectPropertyAuthorize(AuthorizationAction.Write, DefaultRoles.Admin)]
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
- [ ] Add AuthorizationResolverTests.cs
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

**Summary**: Authorization editor panes for subjects, properties, and methods

**Implementation**:

#### 19a: Subject Authorization Button & Pane

1. Add `[🔒 Auth]` button to subject pane header (alongside Edit/Delete)
2. Create `SubjectAuthorizationPane.razor` as **right pane** (follows existing pane pattern)
3. Show all (Kind, AuthorizationAction) combinations with resolved roles:
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
   └─────────────────────────────────────────────────────────────┘
   ```
4. Edit action opens inline role multi-select
5. Clear override (↩️) removes ExtData entry

#### 19b: Property Authorization Icon & Pane

1. Add `🔒` icon next to each property in property grid
2. **Highlight overridden properties** (filled 🔒 icon or subtle background color)
3. Click `🔒` → Opens **right pane** for property authorization:
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
   └─────────────────────────────────────────────────────────────┘
   ```
4. Shows inheritance source on hover/inline
5. Toggle checkbox enables override → shows role multi-select
6. Clear button removes override, reverts to inherited

#### 19c: Method Authorization Icon & Pane

1. Add small `🔒` button **to the right** of each method button:
   ```
   [Toggle] [🔒]    [Factory Reset] [🔒]
   ```
2. **Highlight overridden methods** (filled 🔒 vs outline)
3. Click `🔒` → Opens **right pane** for method authorization:
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
   └─────────────────────────────────────────────────────────────┘
   ```
4. Same pattern as property: show current, source, override toggle

#### Pane Behavior (consistent with existing)
- Opens as right pane (pushes content or overlays)
- Save button persists changes to ExtData
- Close (X) discards unsaved changes (or prompt if dirty)
- Changes write to `subject.Data[(propertyName, "HomeBlaze.Authorization:Kind:Action")]`

**Subtasks**:
- [ ] Tests: Component tests for each pane
- [ ] Tests: Override persistence tests
- [ ] Docs: N/A
- [ ] Code quality: Consistent with existing subject editors
- [ ] Dependencies: No new dependencies
- [ ] Performance: Lazy load property/method lists
- [ ] UX: Highlight overridden items in grid

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
   - Register `IAuthorizationResolver` as **scoped** (for per-request caching)
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
    services.AddScoped<IAuthorizationResolver, AuthorizationResolver>();

    // Use RevalidatingServerAuthenticationStateProvider for Blazor Server
    services.AddScoped<AuthenticationStateProvider, HomeBlazeAuthenticationStateProvider>();

    return services;
}

public static IApplicationBuilder UseAuthorizationContext(this IApplicationBuilder app)
{
    return app.UseMiddleware<AuthorizationContextMiddleware>();
}
```

**Note**: `AuthorizationInterceptor` is registered directly on context via extension method, `IAuthorizationResolver` is resolved via DI

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

**Summary**: Add authorization interceptor to subject context and enable DI resolution

**Current Signature**:
```csharp
public static IInterceptorSubjectContext Create(IServiceCollection services)
```

**New Signature** (breaking change):
```csharp
public static IInterceptorSubjectContext Create(IServiceProvider serviceProvider, IServiceCollection services)
```

**Why Both Parameters**:
- `IServiceProvider` - needed to register on context for DI resolution in interceptors
- `IServiceCollection` - needed for `WithHostedServices()` to register services

**Implementation**:
1. Add `IServiceProvider` parameter to `Create()` method
2. Add `.WithAuthorization()` extension method call
3. Register `IServiceProvider` on context for DI resolution
4. Interceptor uses `[RunsFirst]` so ordering is automatic

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
public static IInterceptorSubjectContext Create(
    IServiceProvider serviceProvider,
    IServiceCollection services)
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
        .WithAuthorization() // NEW: Uses extension method
        .WithDataAnnotationValidation()
        .WithHostedServices(services);

    // CRITICAL: Register IServiceProvider for DI resolution in interceptors
    context.AddService(serviceProvider);

    return context;
}
```

**Breaking Change Migration**:
```csharp
// Before (in Program.cs or similar)
var context = SubjectContextFactory.Create(services);

// After
var app = builder.Build();
var context = SubjectContextFactory.Create(app.Services, builder.Services);
```

**Subtasks**:
- [ ] Tests: Update all tests calling SubjectContextFactory.Create()
- [ ] Tests: Integration tests for authorization
- [ ] Docs: N/A
- [ ] Code quality: Proper ordering
- [ ] Dependencies: HomeBlaze.Authorization
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

### Task 27: Add Audit Logging (Future Enhancement)

**Summary**: Log authorization decisions for security auditing

**Status**: Optional / Future enhancement - not required for initial implementation

**Implementation** (when needed):
1. Create `IAuthorizationAuditLogger` interface
2. Log access denied events with context (user, subject, property, permission)
3. Optional: Log successful access for high-security properties
4. Store in database or external logging service

```csharp
public interface IAuthorizationAuditLogger
{
    void LogAccessDenied(
        ClaimsPrincipal user,
        IInterceptorSubject subject,
        string? propertyName,
        string permission,
        string[] requiredRoles);

    void LogAccessGranted(
        ClaimsPrincipal user,
        IInterceptorSubject subject,
        string? propertyName,
        string permission);
}
```

**Why Optional**:
- Initial implementation focuses on core authorization functionality
- Audit logging adds complexity and storage requirements
- Can be added later without breaking changes

**Subtasks**:
- [ ] Define audit log schema
- [ ] Implement file/database logger
- [ ] Add configuration for audit verbosity
- [ ] Performance: Async logging to avoid blocking

---

## Summary

| Phase | Tasks | Description |
|-------|-------|-------------|
| 1. Infrastructure | 1, 1b, 1c, 1d, 2 | Projects, ISubjectDataProvider, serializer update, provider tests, options |
| 2. Identity & DB | 3-7 | DbContext, role expander, claims transform, seeding, Identity, OAuth |
| 3. Authorization | 8, 8b, 8c, 9-12 | Attributes, AuthorizationDataProvider, role edge case tests, resolver, interceptor, method auth, user context |
| 4. Auth UI | 13-16 | Login/logout, SetPassword, Account, App.razor |
| 5. Admin UI | 17-19 | Users, Roles, Subject panel |
| 6. Filtering | 20-22 | Nav, browser, properties |
| 7. Integration | 23-25 | DI, SubjectContextFactory, Program.cs |
| 8. Session | 26, 27 | Revalidation, Audit logging (optional) |

**Total: 33 tasks across 8 phases** (includes 1b, 1c, 1d for serialization extensibility, 4b for claims, 8b, 8c for persistence/tests, 12b for auth tests, 27 optional)

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
