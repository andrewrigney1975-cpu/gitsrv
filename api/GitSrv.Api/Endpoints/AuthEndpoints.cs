using GitSrv.Api.Auth;

namespace GitSrv.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record RegisterRequest(string Username, string Email, string DisplayName, string Password);
    public sealed record LoginRequest(string UsernameOrEmail, string Password);

    public static void MapAuth(this IEndpointRouteBuilder app, bool cookiesSecure)
    {
        var g = app.MapGroup("/api/auth");

        g.MapPost("/register", async (RegisterRequest req, AccountService accounts, TokenService tokens,
            HttpContext ctx, CancellationToken ct) =>
        {
            await accounts.RegisterAsync(req.Username, req.Email, req.DisplayName ?? "", req.Password, ct);
            var auth = await accounts.AuthenticateAsync(req.Username, req.Password, UserAgent(ctx), ct);
            HttpAuth.SetSessionCookies(ctx.Response, auth!, cookiesSecure);
            return Results.Json(Me(auth!.User));
        });

        g.MapPost("/login", async (LoginRequest req, AccountService accounts, Ops.AuditService audit, HttpContext ctx, CancellationToken ct) =>
        {
            var auth = await accounts.AuthenticateAsync(req.UsernameOrEmail ?? "", req.Password ?? "", UserAgent(ctx), ct);
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
            if (auth is null)
            {
                await audit.LogAsync(null, null, req.UsernameOrEmail ?? "", "login.failed", "", "", ip, ct);
                return Results.Json(new { error = "Incorrect username or password." }, statusCode: 401);
            }
            await audit.LogAsync(null, auth.User.Id, auth.User.Username, "login", "", "", ip, ct);
            HttpAuth.SetSessionCookies(ctx.Response, auth, cookiesSecure);
            return Results.Json(Me(auth.User));
        }).RequireRateLimiting("auth");

        g.MapPost("/refresh", async (AccountService accounts, HttpContext ctx, CancellationToken ct) =>
        {
            var token = ctx.Request.Cookies[HttpAuth.RefreshCookie];
            if (string.IsNullOrEmpty(token))
                return Results.Json(new { error = "No session." }, statusCode: 401);
            var auth = await accounts.RefreshAsync(token, UserAgent(ctx), ct);
            if (auth is null)
            {
                HttpAuth.ClearSessionCookies(ctx.Response);
                return Results.Json(new { error = "Session expired." }, statusCode: 401);
            }
            HttpAuth.SetSessionCookies(ctx.Response, auth, cookiesSecure);
            return Results.Json(Me(auth.User));
        });

        g.MapPost("/logout", async (AccountService accounts, HttpContext ctx, CancellationToken ct) =>
        {
            var token = ctx.Request.Cookies[HttpAuth.RefreshCookie];
            if (!string.IsNullOrEmpty(token))
                await accounts.RevokeAsync(token, ct);
            HttpAuth.ClearSessionCookies(ctx.Response);
            return Results.NoContent();
        });
    }

    internal static object Me(GitSrv.Api.Domain.User u) => new
    {
        u.Id,
        u.Username,
        u.Email,
        u.DisplayName,
        u.IsSiteAdmin,
    };

    private static string UserAgent(HttpContext ctx) => ctx.Request.Headers.UserAgent.ToString();
}
