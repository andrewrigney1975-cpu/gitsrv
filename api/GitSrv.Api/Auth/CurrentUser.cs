using System.Security.Claims;

namespace GitSrv.Api.Auth;

/// <summary>The authenticated principal for the current request, or anonymous.</summary>
public sealed class CurrentUser
{
    public long? UserId { get; private set; }
    public string Username { get; private set; } = "";
    public bool IsSiteAdmin { get; private set; }

    public bool IsAuthenticated => UserId is not null;

    public long RequireId() => UserId ?? throw new UnauthorizedException();

    internal void SetFrom(ClaimsPrincipal principal)
    {
        var sub = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (long.TryParse(sub, out var id))
        {
            UserId = id;
            Username = principal.FindFirstValue("username") ?? "";
            IsSiteAdmin = principal.FindFirstValue("site_admin") == "true";
        }
    }
}

public sealed class UnauthorizedException() : Exception("Authentication required.");

public sealed class ForbiddenException(string message = "You do not have permission to do that.") : Exception(message);

public sealed class NotFoundException(string message = "Not found.") : Exception(message);

public sealed class ValidationException(string message) : Exception(message);
