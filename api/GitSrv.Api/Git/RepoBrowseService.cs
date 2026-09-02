using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using GitSrv.Api.Identity;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Git;

public sealed record BrowseContext(long RepoId, long OrgId, string OrgSlug, string RepoSlug,
    string Name, string Description, string Visibility, string DefaultBranch, bool IsArchived,
    long SizeBytes, DateTime? PushedAt, RepoPermission Permission, string RepoDir,
    string? ImportStatus, string ImportError);

/// <summary>
/// Resolves a browse request to a <see cref="BrowseContext"/> (repo metadata + effective permission
/// + on-disk path), following slug redirects and allowing anonymous read of public repos. Opens a
/// short-lived <see cref="RepoReader"/> for the caller to use and dispose.
/// </summary>
public sealed class RepoBrowseService(Db db, Authorizer authz, GitStorage storage)
{
    private sealed record Row(long Id, long OrgId, string Slug, string Name, string Description,
        string Visibility, string DefaultBranch, bool IsArchived, long SizeBytes, DateTime? PushedAt,
        string? ImportStatus, string ImportError);

    public async Task<BrowseContext> ResolveAsync(string orgSlug, string repoSlug, long? userId, CancellationToken ct)
    {
        orgSlug = Slug.Normalise(orgSlug);
        repoSlug = Slug.Normalise(repoSlug);

        await using var conn = await db.OpenAsync(ct);

        var orgId = await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM organisations WHERE slug = @orgSlug", new { orgSlug })
            ?? await ResolveOrgRedirect(conn, orgSlug);
        if (orgId is null) throw new NotFoundException("Repository not found.");

        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            """
            SELECT id, org_id AS OrgId, slug, name, description, visibility,
                   default_branch AS DefaultBranch, is_archived AS IsArchived,
                   size_bytes AS SizeBytes, pushed_at AS PushedAt,
                   import_status AS ImportStatus, import_error AS ImportError
            FROM repositories WHERE org_id = @orgId AND slug = @repoSlug
            """, new { orgId, repoSlug });

        if (row is null)
        {
            var redirect = await SlugRedirects.ResolveAsync(conn, SlugRedirects.RepoScope(orgId.Value), repoSlug);
            if (redirect is null) throw new NotFoundException("Repository not found.");
            row = await conn.QuerySingleOrDefaultAsync<Row>(
                """
                SELECT id, org_id AS OrgId, slug, name, description, visibility,
                       default_branch AS DefaultBranch, is_archived AS IsArchived,
                       size_bytes AS SizeBytes, pushed_at AS PushedAt,
                       import_status AS ImportStatus, import_error AS ImportError
                FROM repositories WHERE org_id = @orgId AND slug = @redirect
                """, new { orgId, redirect });
            if (row is null) throw new NotFoundException("Repository not found.");
        }

        var perm = await authz.GetRepoPermissionAsync(userId, row.Id, ct);
        if (perm < RepoPermission.Read)
            throw new NotFoundException("Repository not found.");

        var finalOrgSlug = await conn.ExecuteScalarAsync<string>("SELECT slug FROM organisations WHERE id = @id", new { id = row.OrgId }) ?? orgSlug;
        // Don't materialise a bare repo for an import that hasn't landed yet — the worker owns that directory.
        var importPending = row.ImportStatus is "pending" or "importing" or "failed";
        if (!importPending)
            await storage.EnsureAsync(row.OrgId, row.Id, row.DefaultBranch, ct);

        return new BrowseContext(row.Id, row.OrgId, finalOrgSlug, row.Slug, row.Name, row.Description,
            row.Visibility, row.DefaultBranch, row.IsArchived, row.SizeBytes, row.PushedAt, perm,
            storage.RepoPath(row.OrgId, row.Id), row.ImportStatus, row.ImportError ?? "");
    }

    public RepoReader Open(BrowseContext ctx) => new(ctx.RepoDir);

    private static async Task<long?> ResolveOrgRedirect(System.Data.IDbConnection conn, string oldSlug)
    {
        var to = await SlugRedirects.ResolveAsync(conn, SlugRedirects.OrgScope, oldSlug);
        return to is null ? null : await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM organisations WHERE slug = @to", new { to });
    }
}
