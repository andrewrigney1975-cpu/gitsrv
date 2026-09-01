using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Endpoints;

public static class RepoEndpoints
{
    public sealed record UpdateRepoRequest(string Name, string Description, string Visibility, bool IsArchived);
    private sealed record MergeFlags(bool AllowMergeCommit, bool AllowSquash, bool AllowRebase, bool DeleteBranchOnMerge);
    public sealed record RenameRepoRequest(string Slug);
    public sealed record AddCollaboratorRequest(string Username, string Permission);
    public sealed record AddTeamAccessRequest(string TeamSlug, string Permission);

    public static void MapRepos(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}").RequireAuth();

        g.MapGet("/", async (string slug, string repoSlug, HttpContext ctx, CurrentUser cu, OrgService orgs,
            RepoService repos, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct);
            if (org is null)
            {
                var r = await orgs.ResolveRedirectAsync(slug, ct);
                if (r is not null) return Results.Redirect($"/api/orgs/{r}/repos/{repoSlug}", permanent: true);
                throw new NotFoundException("Repository not found.");
            }

            var repo = await repos.GetAsync(org.Id, repoSlug, ct);
            if (repo is null)
            {
                await using var conn = await db.OpenAsync(ct);
                var r = await SlugRedirects.ResolveAsync(conn, SlugRedirects.RepoScope(org.Id), Slug.Normalise(repoSlug));
                if (r is not null) return Results.Redirect($"/api/orgs/{org.Slug}/repos/{r}", permanent: true);
                throw new NotFoundException("Repository not found.");
            }

            var perm = await authz.GetRepoPermissionAsync(cu.RequireId(), repo.Id, ct);
            if (perm == RepoPermission.None)
                throw new NotFoundException("Repository not found.");

            await using var mc = await db.OpenAsync(ct);
            var flags = await mc.QuerySingleAsync<MergeFlags>(
                "SELECT allow_merge_commit AS AllowMergeCommit, allow_squash AS AllowSquash, allow_rebase AS AllowRebase, delete_branch_on_merge AS DeleteBranchOnMerge FROM repositories WHERE id = @id",
                new { id = repo.Id });

            return Results.Json(new
            {
                repo.Id, repo.Slug, repo.Name, repo.Description, repo.Visibility,
                repo.DefaultBranch, repo.IsArchived, orgSlug = org.Slug,
                flags.AllowMergeCommit, flags.AllowSquash, flags.AllowRebase, flags.DeleteBranchOnMerge,
                myPermission = RepoPermissions.ToDbValue(perm),
            });
        });

        g.MapPatch("/", async (string slug, string repoSlug, UpdateRepoRequest req, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await repos.UpdateSettingsAsync(repo.Id, req.Name ?? repo.Name, req.Description ?? repo.Description,
                req.Visibility ?? repo.Visibility, req.IsArchived, ct);
            return Results.NoContent();
        });

        g.MapPost("/rename", async (string slug, string repoSlug, RenameRepoRequest req, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await repos.RenameSlugAsync(repo.Id, req.Slug ?? "", ct);
            return Results.NoContent();
        });

        g.MapDelete("/", async (string slug, string repoSlug, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await repos.DeleteAsync(repo.OrgId, repo.Id, ct);
            return Results.NoContent();
        });

        // ---- collaborators & team access ----
        g.MapGet("/collaborators", async (string slug, string repoSlug, CurrentUser cu, OrgService orgs,
            RepoService repos, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await using var conn = await db.OpenAsync(ct);
            var users = await conn.QueryAsync(
                """
                SELECT u.id AS userId, u.username, u.display_name AS displayName, c.permission
                FROM repo_collaborators c JOIN users u ON u.id = c.user_id
                WHERE c.repo_id = @id ORDER BY u.username
                """, new { id = repo.Id });
            var teams = await conn.QueryAsync(
                """
                SELECT t.id AS teamId, t.slug, t.name, rta.permission
                FROM repo_team_access rta JOIN teams t ON t.id = rta.team_id
                WHERE rta.repo_id = @id ORDER BY t.name
                """, new { id = repo.Id });
            return Results.Json(new { users, teams });
        });

        g.MapPost("/collaborators", async (string slug, string repoSlug, AddCollaboratorRequest req, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            var permission = RepoPermissions.ToDbValue(RepoPermissions.Parse(req.Permission));
            await using var conn = await db.OpenAsync(ct);
            var userId = await conn.QuerySingleOrDefaultAsync<long?>(
                "SELECT id FROM users WHERE username = @u", new { u = Slug.Normalise(req.Username ?? "") })
                ?? throw new ValidationException($"No user '{req.Username}'.");
            await conn.ExecuteAsync(
                """
                INSERT INTO repo_collaborators (repo_id, user_id, permission) VALUES (@repoId, @userId, @permission)
                ON CONFLICT (repo_id, user_id) DO UPDATE SET permission = EXCLUDED.permission
                """, new { repoId = repo.Id, userId, permission });
            return Results.NoContent();
        });

        g.MapDelete("/collaborators/{userId:long}", async (string slug, string repoSlug, long userId, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync("DELETE FROM repo_collaborators WHERE repo_id = @repoId AND user_id = @userId",
                new { repoId = repo.Id, userId });
            return Results.NoContent();
        });

        g.MapPost("/team-access", async (string slug, string repoSlug, AddTeamAccessRequest req, CurrentUser cu,
            OrgService orgs, RepoService repos, TeamService teams, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Repository not found.");
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            var team = await teams.GetAsync(org.Id, req.TeamSlug ?? "", ct) ?? throw new ValidationException("No such team.");
            var permission = RepoPermissions.ToDbValue(RepoPermissions.Parse(req.Permission));
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync(
                """
                INSERT INTO repo_team_access (repo_id, team_id, permission) VALUES (@repoId, @teamId, @permission)
                ON CONFLICT (repo_id, team_id) DO UPDATE SET permission = EXCLUDED.permission
                """, new { repoId = repo.Id, teamId = team.Id, permission });
            return Results.NoContent();
        });

        g.MapDelete("/team-access/{teamId:long}", async (string slug, string repoSlug, long teamId, CurrentUser cu,
            OrgService orgs, RepoService repos, Authorizer authz, Db db, CancellationToken ct) =>
        {
            var repo = await RequireRepoAsync(orgs, repos, authz, cu, slug, repoSlug, RepoPermission.Admin, ct);
            await using var conn = await db.OpenAsync(ct);
            await conn.ExecuteAsync("DELETE FROM repo_team_access WHERE repo_id = @repoId AND team_id = @teamId",
                new { repoId = repo.Id, teamId });
            return Results.NoContent();
        });
    }

    private static async Task<GitSrv.Api.Domain.Repository> RequireRepoAsync(OrgService orgs, RepoService repos,
        Authorizer authz, CurrentUser cu, string slug, string repoSlug, RepoPermission minimum, CancellationToken ct)
    {
        var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Repository not found.");
        var repo = await repos.GetAsync(org.Id, repoSlug, ct) ?? throw new NotFoundException("Repository not found.");
        await authz.RequireRepoAsync(cu.RequireId(), repo.Id, minimum, ct);
        return repo;
    }
}
