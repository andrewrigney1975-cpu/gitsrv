namespace GitSrv.Api.Auth;

public static class HttpAuth
{
    public const string AccessCookie = "gs_access";
    public const string RefreshCookie = "gs_refresh";
    public const string CsrfHeader = "X-GitSrv-CSRF";

    public static void SetSessionCookies(HttpResponse res, AuthResult auth, bool secure)
    {
        res.Cookies.Append(AccessCookie, auth.AccessJwt, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            Expires = auth.AccessExpires,
        });
        res.Cookies.Append(RefreshCookie, auth.Refresh.Token, new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth",
            Expires = auth.Refresh.ExpiresAt,
        });
    }

    public static void ClearSessionCookies(HttpResponse res)
    {
        res.Cookies.Delete(AccessCookie, new CookieOptions { Path = "/" });
        res.Cookies.Delete(RefreshCookie, new CookieOptions { Path = "/api/auth" });
    }
}

/// <summary>
/// Rejects state-changing API calls that don't carry the custom CSRF header. Because the header is
/// custom and CORS is not enabled, a browser on another origin cannot set it, so its mere presence
/// is enough — no token to compare. GET/HEAD/OPTIONS and the git transports (added in Phase 2, which
/// authenticate with Basic/PAT, not cookies) are exempt.
/// </summary>
public sealed class CsrfMiddleware(RequestDelegate next)
{
    public async Task Invoke(HttpContext ctx)
    {
        var m = ctx.Request.Method;
        var isUnsafe = m is not ("GET" or "HEAD" or "OPTIONS" or "TRACE");
        var isApi = ctx.Request.Path.StartsWithSegments("/api");
        var usesCookieAuth = ctx.Request.Cookies.ContainsKey(HttpAuth.AccessCookie)
                             || ctx.Request.Cookies.ContainsKey(HttpAuth.RefreshCookie);

        if (isUnsafe && isApi && usesCookieAuth && !ctx.Request.Headers.ContainsKey(HttpAuth.CsrfHeader))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { error = $"Missing {HttpAuth.CsrfHeader} header." });
            return;
        }

        await next(ctx);
    }
}
