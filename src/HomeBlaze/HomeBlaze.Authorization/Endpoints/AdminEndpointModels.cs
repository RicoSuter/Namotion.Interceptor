namespace HomeBlaze.Authorization.Endpoints;

/// <summary>
/// User information returned by admin endpoints.
/// </summary>
public class UserDto
{
    public string Id { get; set; } = "";
    public string UserName { get; set; } = "";
    public string? Email { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public List<string> Roles { get; set; } = [];
}

/// <summary>
/// Request to create a new user.
/// </summary>
public class CreateUserRequest
{
    public string UserName { get; set; } = "";
    public string? Email { get; set; }
    public List<string>? Roles { get; set; }
}

/// <summary>
/// Request to update an existing user.
/// </summary>
public class UpdateUserRequest
{
    public string? Email { get; set; }
    public List<string>? Roles { get; set; }
}

/// <summary>
/// Role information returned by admin endpoints.
/// </summary>
public class RoleDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>
/// Request to create a new role.
/// </summary>
public class CreateRoleRequest
{
    public string Name { get; set; } = "";
}

/// <summary>
/// Generic paged result for list endpoints.
/// </summary>
/// <typeparam name="T">The type of items in the result.</typeparam>
public class PagedResult<T>
{
    public List<T> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
