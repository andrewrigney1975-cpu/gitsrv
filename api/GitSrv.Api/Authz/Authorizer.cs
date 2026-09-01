using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;

namespace GitSrv.Api.Authz;

/// <summary>
/// DB-backed authorization. Loads the facts for a (user, resource) pair and defers the actual
/// decision to <see cref="PermissionResolver"/>. This is the one place the rest of the API asks
/// "can this user do X?" — endpoints never query membership tables themselves.
/// </summary>
public sealed class Authorizer(Db db)
{
    private sealed record RepoFactsRow(string Visibility, bool IsArchived, long OrgId);

    public async Task<bool> IsSiteAdminAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>("SELECT is_site_admin FROM users WHERE id = @userId", new { userId });
    }

    public async Task<OrgRole?> GetOrgRoleAsync(long userId, long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var role = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT role FROM org_members WHERE org_id = @orgId AND user_id = @userId", new { orgId, userId });
        return role is null ? null : OrgRoles.Parse(role);
    }

    public async Task RequireOrgRoleAsync(long userId, long orgId, OrgRole minimum, CancellationToken ct)
    {
        if (await IsSiteAdminAsync(userId, ct))
            return;
        var role = await GetOrgRoleAsync(userId, orgId, ct);
        if (role is null || role < minimum)
            throw new ForbiddenException($"This action requires the '{minimum.ToString().ToLowerInvariant()}' org role.");
    }

    public async Task<RepoPermission> GetRepoPermissionAsync(long userId, long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);

        var repo = await conn.QuerySingleOrDefaultAsync<RepoFactsRow>(
            "SELECT visibility, is_archived AS IsArchived, org_id AS OrgId FROM repositories WHERE id = @repoId", new { repoId });
        if (repo is null)
            throw new NotFoundException("Repository not found.");

        var isSiteAdmin = await conn.ExecuteScalarAsync<bool>("SELECT is_site_admin FROM users WHERE id = @userId", new { userId });
        var orgRoleRaw = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT role FROM org_members WHERE org_id = @orgId AND user_id = @userId", new { repo.OrgId, userId });
        var direct = await conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT permission FROM repo_collaborators WHERE repo_id = @repoId AND user_id = @userId", new { repoId, userId });
        var teamGrants = (await conn.QueryAsync<string>(
            """
            SELECT rta.permission
            FROM repo_team_access rta
            JOIN team_members tm ON tm.team_id = rta.team_id
            WHERE rta.repo_id = @repoId AND tm.user_id = @userId
            """, new { repoId, userId })).ToList();

        var facts = new RepoAccessFacts
        {
            IsSiteAdmin = isSiteAdmin,
            OrgRole = orgRoleRaw is null ? null : OrgRoles.Parse(orgRoleRaw),
            Visibility = repo.Visibility,
            IsArchived = repo.IsArchived,
            DirectGrant = RepoPermissions.Parse(direct),
            TeamGrants = teamGrants.Select(RepoPermissions.Parse).ToList(),
        };

        return PermissionResolver.ResolveRepo(facts);
    }

    public async Task RequireRepoAsync(long userId, long repoId, RepoPermission minimum, CancellationToken ct)
    {
        var have = await GetRepoPermissionAsync(userId, repoId, ct);
        if (have < minimum)
        {
            // Don't disclose existence of a repo the caller can't even read.
            if (have == RepoPermission.None)
                throw new NotFoundException("Repository not found.");
            throw new ForbiddenException();
        }
    }
}
