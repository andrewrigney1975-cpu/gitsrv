using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Identity;

namespace GitSrv.Api.Git;

public enum GitOperation { Read, Write }

public sealed record GitTarget(long RepoId, long OrgId, string OrgSlug, string RepoSlug, string DefaultBranch, string AbsolutePath);

/// <summary>
/// Resolves a git request ("<c>acme/widget.git</c>", read or write, as user X") to an on-disk
/// path, enforcing <see cref="Authorizer"/> and lazily materialising the bare repo. Shared by the
/// HTTP transport and the SSH shim.
/// </summary>
public sealed class GitAccessService(Db db, Authorizer authz, GitStorage storage)
{
    private sealed record RepoRow(long Id, long OrgId, string Slug, string DefaultBranch, string Visibility);

    /// <summary>
    /// Parses "<c>[/]owner/name[.git]</c>" and resolves it. Returns null if the path is malformed
    /// or the org/repo does not exist. Throws <see cref="ForbiddenException"/> when the repo exists
    /// but the principal lacks the operation; returns null (not throw) when the repo is unreadable,
    /// so callers can 404 without disclosing existence.
    /// </summary>
    public async Task<GitTarget?> ResolveAsync(string rawPath, long? userId, GitOperation op, CancellationToken ct, bool trusted = false)
    {
        var parts = rawPath.Trim('/').Split('/');
        if (parts.Length != 2)
            return null;

        var orgSlug = Slug.Normalise(parts[0]);
        var repoSlug = Slug.Normalise(parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? parts[1][..^4] : parts[1]);

        await using var conn = await db.OpenAsync(ct);

        var orgId = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM organisations WHERE slug = @orgSlug", new { orgSlug });
        if (orgId is null)
        {
            var newOrg = await SlugRedirects.ResolveAsync(conn, SlugRedirects.OrgScope, orgSlug);
            if (newOrg is null) return null;
            orgId = await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM organisations WHERE slug = @newOrg", new { newOrg });
            if (orgId is null) return null;
        }

        var repo = await conn.QuerySingleOrDefaultAsync<RepoRow>(
            """
            SELECT id, org_id AS OrgId, slug, default_branch AS DefaultBranch, visibility
            FROM repositories WHERE org_id = @orgId AND slug = @repoSlug
            """, new { orgId, repoSlug });
        if (repo is null)
        {
            var newRepo = await SlugRedirects.ResolveAsync(conn, SlugRedirects.RepoScope(orgId.Value), repoSlug);
            if (newRepo is null) return null;
            repo = await conn.QuerySingleOrDefaultAsync<RepoRow>(
                """
                SELECT id, org_id AS OrgId, slug, default_branch AS DefaultBranch, visibility
                FROM repositories WHERE org_id = @orgId AND slug = @newRepo
                """, new { orgId, newRepo });
            if (repo is null) return null;
        }

        var have = trusted ? RepoPermission.Admin
            : userId is null
                ? (repo.Visibility == "public" ? RepoPermission.Read : RepoPermission.None)
                : await authz.GetRepoPermissionAsync(userId.Value, repo.Id, ct);

        var need = op == GitOperation.Write ? RepoPermission.Write : RepoPermission.Read;
        if (have < need)
        {
            if (have == RepoPermission.None)
                return null; // unreadable — caller 404s
            throw new ForbiddenException(op == GitOperation.Write
                ? "You have read access but not write access to this repository."
                : "You do not have access to this repository.");
        }

        await storage.EnsureAsync(repo.OrgId, repo.Id, repo.DefaultBranch, ct);

        var orgSlugFinal = await conn.ExecuteScalarAsync<string>("SELECT slug FROM organisations WHERE id = @id", new { id = repo.OrgId }) ?? orgSlug;
        return new GitTarget(repo.Id, repo.OrgId, orgSlugFinal, repo.Slug, repo.DefaultBranch,
            storage.RepoPath(repo.OrgId, repo.Id));
    }

    /// <summary>Refresh size + pushed_at after a receive-pack. Best-effort.</summary>
    public async Task RecordPushAsync(GitTarget target, CancellationToken ct)
    {
        var size = storage.MeasureSize(target.OrgId, target.RepoId);
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE repositories SET size_bytes = @size, pushed_at = now(), updated_at = now() WHERE id = @id",
            new { size, id = target.RepoId });
    }
}
