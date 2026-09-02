using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Domain;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

public static class OrgEndpoints
{
    public sealed record CreateOrgRequest(string Slug, string Name, string Description);
    public sealed record UpdateOrgRequest(string Name, string Description);
    public sealed record RenameRequest(string Slug);
    public sealed record AddMemberRequest(string Username, string Role);
    public sealed record SetRoleRequest(string Role);
    public sealed record CreateTeamRequest(string Slug, string Name, string Description);
    public sealed record UpdateTeamRequest(string Name, string Description);
    public sealed record AddTeamMemberRequest(string Username);
    public sealed record CreateRepoRequest(string Slug, string Name, string Description, string Visibility, string DefaultBranch);
    public sealed record ImportRepoRequest(string Slug, string Name, string Visibility, string SourceUrl);

    public static void MapOrgs(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs").RequireAuth();

        // ---- org CRUD ----
        g.MapPost("/", async (CreateOrgRequest req, CurrentUser cu, OrgService orgs, CancellationToken ct) =>
        {
            var org = await orgs.CreateAsync(cu.RequireId(), req.Slug ?? "", req.Name ?? "", req.Description ?? "", ct);
            return Results.Json(new { org.Id, org.Slug, org.Name, org.Description }, statusCode: 201);
        });

        g.MapGet("/{slug}", async (string slug, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            var (org, redirect) = await ResolveAsync(orgs, slug, ct);
            if (redirect is not null) return Results.Redirect($"/api/orgs/{redirect}", permanent: true);
            var role = await authz.GetOrgRoleAsync(cu.RequireId(), org!.Id, ct);
            var siteAdmin = await authz.IsSiteAdminAsync(cu.RequireId(), ct);
            if (role is null && !siteAdmin)
                throw new NotFoundException("Organisation not found.");
            return Results.Json(new
            {
                org.Id, org.Slug, org.Name, org.Description,
                myRole = role?.ToString().ToLowerInvariant() ?? (siteAdmin ? "site-admin" : null),
            });
        });

        g.MapPatch("/{slug}", async (string slug, UpdateOrgRequest req, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            await orgs.UpdateAsync(org.Id, req.Name ?? "", req.Description ?? "", ct);
            return Results.NoContent();
        });

        g.MapPost("/{slug}/rename", async (string slug, RenameRequest req, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Owner, ct);
            await orgs.RenameSlugAsync(org.Id, req.Slug ?? "", ct);
            return Results.NoContent();
        });

        // ---- members ----
        g.MapGet("/{slug}/members", async (string slug, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Member, ct);
            return Results.Json(await orgs.ListMembersAsync(org.Id, ct));
        });

        g.MapPost("/{slug}/members", async (string slug, AddMemberRequest req, CurrentUser cu, OrgService orgs, Authorizer authz,
            Ops.AuditService audit, HttpContext ctx, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            await orgs.AddMemberAsync(org.Id, req.Username ?? "", req.Role ?? "member", ct);
            await audit.LogAsync(org.Id, cu.RequireId(), cu.Username, "member.add", req.Username ?? "", $"role={req.Role}", ctx.Connection.RemoteIpAddress?.ToString() ?? "", ct);
            return Results.NoContent();
        });

        g.MapPatch("/{slug}/members/{userId:long}", async (string slug, long userId, SetRoleRequest req, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Owner, ct);
            await orgs.SetMemberRoleAsync(org.Id, userId, req.Role ?? "member", ct);
            return Results.NoContent();
        });

        g.MapDelete("/{slug}/members/{userId:long}", async (string slug, long userId, CurrentUser cu, OrgService orgs, Authorizer authz, CancellationToken ct) =>
        {
            // Admins can remove members; owners required to remove another owner (guarded by last-owner check anyway).
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            await orgs.RemoveMemberAsync(org.Id, userId, ct);
            return Results.NoContent();
        });

        // ---- teams ----
        g.MapGet("/{slug}/teams", async (string slug, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Member, ct);
            return Results.Json(await teams.ListAsync(org.Id, ct));
        });

        g.MapPost("/{slug}/teams", async (string slug, CreateTeamRequest req, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            var team = await teams.CreateAsync(org.Id, req.Slug ?? "", req.Name ?? "", req.Description ?? "", ct);
            return Results.Json(new { team.Id, team.Slug, team.Name }, statusCode: 201);
        });

        g.MapGet("/{slug}/teams/{teamSlug}", async (string slug, string teamSlug, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Member, ct);
            var team = await teams.GetAsync(org.Id, teamSlug, ct) ?? throw new NotFoundException("Team not found.");
            return Results.Json(new { team.Id, team.Slug, team.Name, team.Description, members = await teams.ListMembersAsync(team.Id, ct) });
        });

        g.MapPatch("/{slug}/teams/{teamSlug}", async (string slug, string teamSlug, UpdateTeamRequest req, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            var team = await teams.GetAsync(org.Id, teamSlug, ct) ?? throw new NotFoundException("Team not found.");
            await teams.UpdateAsync(team.Id, req.Name ?? "", req.Description ?? "", ct);
            return Results.NoContent();
        });

        g.MapDelete("/{slug}/teams/{teamSlug}", async (string slug, string teamSlug, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            var team = await teams.GetAsync(org.Id, teamSlug, ct) ?? throw new NotFoundException("Team not found.");
            await teams.DeleteAsync(team.Id, ct);
            return Results.NoContent();
        });

        g.MapPost("/{slug}/teams/{teamSlug}/members", async (string slug, string teamSlug, AddTeamMemberRequest req, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            var team = await teams.GetAsync(org.Id, teamSlug, ct) ?? throw new NotFoundException("Team not found.");
            await teams.AddMemberAsync(org.Id, team.Id, req.Username ?? "", ct);
            return Results.NoContent();
        });

        g.MapDelete("/{slug}/teams/{teamSlug}/members/{userId:long}", async (string slug, string teamSlug, long userId, CurrentUser cu, OrgService orgs, TeamService teams, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Admin, ct);
            var team = await teams.GetAsync(org.Id, teamSlug, ct) ?? throw new NotFoundException("Team not found.");
            await teams.RemoveMemberAsync(team.Id, userId, ct);
            return Results.NoContent();
        });

        // ---- repos (records only in Phase 1) ----
        g.MapGet("/{slug}/repos", async (string slug, CurrentUser cu, OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var (org, redirect) = await ResolveAsync(orgs, slug, ct);
            if (redirect is not null) return Results.Redirect($"/api/orgs/{redirect}/repos", permanent: true);
            if (org is null) throw new NotFoundException("Organisation not found.");
            return Results.Json(await repos.ListVisibleAsync(org.Id, cu.UserId, ct));
        });

        g.MapPost("/{slug}/repos", async (string slug, CreateRepoRequest req, CurrentUser cu, OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Member, ct);
            var repo = await repos.CreateAsync(org.Id, cu.RequireId(), req.Slug ?? "", req.Name ?? "",
                req.Description ?? "", req.Visibility ?? "private", req.DefaultBranch ?? "main", ct);
            return Results.Json(new { repo.Id, repo.Slug, repo.Name, repo.Visibility, orgSlug = org.Slug }, statusCode: 201);
        });

        // Import an external repo by clone URL — a background worker mirrors it into the bare repo.
        g.MapPost("/{slug}/repos/import", async (string slug, ImportRepoRequest req, CurrentUser cu, OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var org = await RequireOrgAsync(orgs, authz, cu, slug, OrgRole.Member, ct);
            await repos.CreateImportAsync(org.Id, cu.RequireId(), req.Slug ?? "", req.Name ?? "",
                req.Visibility ?? "private", req.SourceUrl ?? "", ct);
            return Results.Json(new { slug = Slug.Normalise(req.Slug ?? ""), orgSlug = org.Slug }, statusCode: 202);
        });
    }

    // ---- shared resolution helpers ----

    internal static async Task<(Organisation? Org, string? RedirectSlug)> ResolveAsync(OrgService orgs, string slug, CancellationToken ct)
    {
        var org = await orgs.GetBySlugAsync(slug, ct);
        if (org is not null) return (org, null);
        var redirect = await orgs.ResolveRedirectAsync(slug, ct);
        return (null, redirect);
    }

    internal static async Task<Organisation> RequireOrgAsync(OrgService orgs, Authorizer authz, CurrentUser cu, string slug, OrgRole minimum, CancellationToken ct)
    {
        var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
        await authz.RequireOrgRoleAsync(cu.RequireId(), org.Id, minimum, ct);
        return org;
    }
}
