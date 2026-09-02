using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

/// <summary>Slug suggestion + availability, for the "new org / repo / team" forms.</summary>
public static class SlugEndpoints
{
    public static void MapSlugs(this IEndpointRouteBuilder app)
    {
        // Suggest a slug from a display name (mirrors Slug.Suggest; the front end also does this locally).
        app.MapGet("/api/slug-suggest", (string name) => Results.Json(new { suggestion = Slug.Suggest(name ?? "") }));

        // Org slugs are unique platform-wide.
        app.MapGet("/api/slug-available/org", async (string slug, CurrentUser cu, Db db, CancellationToken ct) =>
        {
            if (!cu.IsAuthenticated) throw new UnauthorizedException();
            slug = Slug.Normalise(slug ?? "");
            if (!Slug.IsValid(slug)) return Results.Json(new { available = false, reason = "invalid" });
            await using var conn = await db.OpenAsync(ct);
            var taken = await conn.ExecuteScalarAsync<bool>("""
                SELECT EXISTS (SELECT 1 FROM organisations WHERE slug = @slug)
                    OR EXISTS (SELECT 1 FROM slug_redirects WHERE scope = 'org' AND old_slug = @slug)
                """, new { slug });
            return Results.Json(new { available = !taken });
        }).RequireAuth();

        // Repo / team slugs are unique within the org; caller must be a member.
        app.MapGet("/api/orgs/{orgSlug}/slug-available", async (string orgSlug, string kind, string slug,
            CurrentUser cu, OrgService orgs, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(orgSlug, ct) ?? throw new NotFoundException("Organisation not found.");
            await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, OrgRole.Member, ct);

            slug = Slug.Normalise(slug ?? "");
            if (!Slug.IsValid(slug)) return Results.Json(new { available = false, reason = "invalid" });

            await using var conn = await db.OpenAsync(ct);
            bool taken = kind switch
            {
                "team" => await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM teams WHERE org_id = @orgId AND slug = @slug)", new { orgId = org.Id, slug }),
                _ => await conn.ExecuteScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM repositories WHERE org_id = @orgId AND slug = @slug)
                        OR EXISTS (SELECT 1 FROM slug_redirects WHERE scope = @scope AND old_slug = @slug)
                    """, new { orgId = org.Id, slug, scope = SlugRedirects.RepoScope(org.Id) }),
            };
            return Results.Json(new { available = !taken });
        }).RequireAuth();
    }
}
