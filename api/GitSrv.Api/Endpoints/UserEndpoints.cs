using GitSrv.Api.Auth;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

public static class UserEndpoints
{
    public sealed record UpdateProfileRequest(string DisplayName);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record AddSshKeyRequest(string Title, string Key);

    public static void MapUsers(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/user").RequireAuth();

        me.MapGet("/", async (CurrentUser cu, AccountService accounts, OrgService orgs, CancellationToken ct) =>
        {
            var user = await accounts.GetAsync(cu.RequireId(), ct) ?? throw new NotFoundException("User not found.");
            var myOrgs = await orgs.ListForUserAsync(user.Id, ct);
            return Results.Json(new
            {
                user = AuthEndpoints.Me(user),
                organisations = myOrgs,
            });
        });

        me.MapPatch("/profile", async (UpdateProfileRequest req, CurrentUser cu, AccountService accounts, CancellationToken ct) =>
        {
            await accounts.UpdateProfileAsync(cu.RequireId(), req.DisplayName ?? "", ct);
            return Results.NoContent();
        });

        me.MapPost("/password", async (ChangePasswordRequest req, CurrentUser cu, AccountService accounts, HttpContext ctx, CancellationToken ct) =>
        {
            await accounts.ChangePasswordAsync(cu.RequireId(), req.CurrentPassword ?? "", req.NewPassword ?? "", ct);
            HttpAuth.ClearSessionCookies(ctx.Response);
            return Results.NoContent();
        });

        me.MapGet("/keys", async (CurrentUser cu, SshKeyService keys, CancellationToken ct) =>
            Results.Json(await keys.ListAsync(cu.RequireId(), ct)));

        me.MapPost("/keys", async (AddSshKeyRequest req, CurrentUser cu, SshKeyService keys, CancellationToken ct) =>
        {
            var key = await keys.AddAsync(cu.RequireId(), req.Title ?? "", req.Key ?? "", ct);
            return Results.Json(key, statusCode: 201);
        });

        me.MapDelete("/keys/{id:long}", async (long id, CurrentUser cu, SshKeyService keys, CancellationToken ct) =>
        {
            await keys.RemoveAsync(cu.RequireId(), id, ct);
            return Results.NoContent();
        });

        // ---- personal access tokens ----
        me.MapGet("/tokens", async (CurrentUser cu, PatService pats, CancellationToken ct) =>
            Results.Json(await pats.ListAsync(cu.RequireId(), ct)));

        me.MapPost("/tokens", async (CreateTokenRequest req, CurrentUser cu, PatService pats, CancellationToken ct) =>
        {
            DateTime? expires = req.ExpiresInDays is > 0 ? DateTime.UtcNow.AddDays(req.ExpiresInDays.Value) : null;
            var (summary, token) = await pats.CreateAsync(cu.RequireId(), req.Name ?? "",
                req.ScopeRead ?? true, req.ScopeWrite ?? true, expires, ct);
            // The raw token is returned exactly once.
            return Results.Json(new { token, summary }, statusCode: 201);
        });

        me.MapDelete("/tokens/{id:long}", async (long id, CurrentUser cu, PatService pats, CancellationToken ct) =>
        {
            await pats.RevokeAsync(cu.RequireId(), id, ct);
            return Results.NoContent();
        });
    }

    public sealed record CreateTokenRequest(string Name, bool? ScopeRead, bool? ScopeWrite, int? ExpiresInDays);
}
