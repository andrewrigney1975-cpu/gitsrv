using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Domain;
using GitSrv.Api.Identity;
using GitSrv.Api.Integrations;

namespace GitSrv.Api.Endpoints;

public static class EnklrEndpoints
{
    public sealed record ConnectRequest(string BaseUrl, string Workspace, string ApiToken, string InboundSecret, string CardPrefix);

    public static void MapEnklr(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/enklr");

        g.MapGet("/", async (string slug, CurrentUser cu, OrgService orgs, Authorizer authz, EnklrService enklr, CancellationToken ct) =>
        {
            var org = await RequireAdmin(slug, cu, orgs, authz, ct);
            var c = await enklr.GetForOrgAsync(org.Id, ct);
            return Results.Json(c is null ? new { connected = false } : new
            {
                connected = true, c.BaseUrl, c.Workspace, c.CardPrefix, c.IsActive,
            });
        });

        g.MapPut("/", async (string slug, ConnectRequest req, CurrentUser cu, OrgService orgs, Authorizer authz, EnklrService enklr, CancellationToken ct) =>
        {
            var org = await RequireAdmin(slug, cu, orgs, authz, ct);
            var id = await enklr.ConnectAsync(org.Id, cu.RequireId(), req.BaseUrl ?? "", req.Workspace ?? "",
                req.ApiToken ?? "", req.InboundSecret ?? "", req.CardPrefix ?? "ENK", ct);
            return Results.Json(new { id });
        });

        g.MapDelete("/", async (string slug, CurrentUser cu, OrgService orgs, Authorizer authz, EnklrService enklr, CancellationToken ct) =>
        {
            var org = await RequireAdmin(slug, cu, orgs, authz, ct);
            await enklr.DisconnectAsync(org.Id, ct);
            return Results.NoContent();
        });

        // Enklr (or a GitSrv user) fetches the work linked to a card.
        g.MapGet("/cards/{cardRef}", async (string slug, string cardRef, CurrentUser cu, OrgService orgs, Authorizer authz, EnklrService enklr, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            if (cu.IsAuthenticated)
            {
                var role = await authz.GetOrgRoleAsync(cu.RequireId(), org.Id, ct);
                if (role is null && !await authz.IsSiteAdminAsync(cu.RequireId(), ct)) throw new ForbiddenException();
            }
            else throw new UnauthorizedException();
            return Results.Json(await enklr.LinksForCardAsync(org.Id, cardRef, ct));
        });

        // Reverse direction: Enklr posts card events (HMAC-verified) — e.g. a card moved to "In Progress".
        app.MapPost("/api/integrations/enklr/{connectionId:long}/events", async (long connectionId, HttpContext ctx, EnklrService enklr, CancellationToken ct) =>
        {
            var c = await enklr.GetAsync(connectionId, ct) ?? throw new NotFoundException("Connection not found.");
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            var sig = ctx.Request.Headers["X-Enklr-Signature-256"].ToString();
            if (!await enklr.VerifyInboundAsync(connectionId, sig, body, ct))
                return Results.StatusCode(401);

            var evt = "unknown";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("type", out var t)) evt = t.GetString() ?? "unknown";
            }
            catch { /* keep 'unknown' */ }
            await enklr.RecordInboundAsync(connectionId, evt, System.Text.Encoding.UTF8.GetString(body).Trim(), ct);
            return Results.Accepted();
        });
    }

    private static async Task<Organisation> RequireAdmin(string slug, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct)
    {
        var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
        await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, OrgRole.Admin, ct);
        return org;
    }
}
