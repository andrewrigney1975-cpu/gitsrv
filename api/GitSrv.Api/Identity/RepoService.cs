using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;
using GitSrv.Api.Git;

namespace GitSrv.Api.Identity;

public sealed record RepoSummary(long Id, string Slug, string Name, string Description, string Visibility, bool IsArchived);

public sealed class RepoService(Db db, GitStorage storage)
{
    private static readonly string[] Visibilities = ["public", "internal", "private"];

    public async Task<Repository> CreateAsync(long orgId, long creatorUserId, string slug, string name,
        string description, string visibility, string defaultBranch, CancellationToken ct)
    {
        slug = Slug.Normalise(slug);
        if (!Slug.IsValid(slug))
            throw new ValidationException("Repo slug must be 1–40 chars, lowercase letters/digits with single - or _ separators, and not reserved.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Repo name is required and must be 100 chars or fewer.");
        if (!Visibilities.Contains(visibility))
            throw new ValidationException("Visibility must be public, internal or private.");
        defaultBranch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch.Trim();

        await using var conn = await db.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repositories WHERE org_id = @orgId AND slug = @slug)", new { orgId, slug }))
            throw new ValidationException("A repository with that slug already exists in this org.");

        var id = await conn.ExecuteScalarAsync<long>(
            """
            INSERT INTO repositories (org_id, slug, name, description, visibility, default_branch, created_by)
            VALUES (@orgId, @slug, @name, @description, @visibility, @defaultBranch, @creatorUserId)
            RETURNING id
            """,
            new { orgId, slug, name = name.Trim(), description = description.Trim(), visibility, defaultBranch, creatorUserId });

        try
        {
            await storage.EnsureAsync(orgId, id, defaultBranch, ct);
        }
        catch
        {
            await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @id", new { id });
            throw;
        }

        return new Repository(id, orgId, slug, name.Trim(), description.Trim(), visibility, defaultBranch, false, creatorUserId, DateTime.UtcNow);
    }

    /// <summary>
    /// Creates a repo record marked for import. A background worker (RepoImportWorker) clones
    /// <paramref name="sourceUrl"/> into the bare repo; the record shows import_status until done.
    /// </summary>
    public async Task CreateImportAsync(long orgId, long creatorUserId, string slug, string name,
        string visibility, string sourceUrl, CancellationToken ct)
    {
        slug = Slug.Normalise(slug);
        if (!Slug.IsValid(slug))
            throw new ValidationException("Repo slug must be 1–40 chars, lowercase letters/digits with single - or _ separators, and not reserved.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Repo name is required and must be 100 chars or fewer.");
        if (!Visibilities.Contains(visibility))
            throw new ValidationException("Visibility must be public, internal or private.");
        Ops.UrlGuard.EnsureSafe(sourceUrl);
        if (!sourceUrl.EndsWith(".git") && !sourceUrl.Contains("://"))
            throw new ValidationException("Enter a full clone URL, e.g. https://github.com/owner/repo.git");

        await using var conn = await db.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM repositories WHERE org_id = @orgId AND slug = @slug)", new { orgId, slug }))
            throw new ValidationException("A repository with that slug already exists in this org.");

        await conn.ExecuteAsync("""
            INSERT INTO repositories (org_id, slug, name, visibility, default_branch, created_by, import_source, import_status)
            VALUES (@orgId, @slug, @name, @visibility, 'main', @creatorUserId, @sourceUrl, 'pending')
            """, new { orgId, slug, name = name.Trim(), visibility, creatorUserId, sourceUrl = sourceUrl.Trim() });
    }

    public async Task<Repository?> GetAsync(long orgId, string slug, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Repository>(
            """
            SELECT id, org_id, slug, name, description, visibility, default_branch, is_archived, created_by, created_at
            FROM repositories WHERE org_id = @orgId AND slug = @slug
            """, new { orgId, slug = Slug.Normalise(slug) });
    }

    /// <summary>Repos in an org that the given user is allowed to see, cheaply (no per-repo resolve).</summary>
    public async Task<IReadOnlyList<RepoSummary>> ListVisibleAsync(long orgId, long? userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<RepoSummary>(
            """
            SELECT DISTINCT r.id, r.slug, r.name, r.description, r.visibility, r.is_archived
            FROM repositories r
            WHERE r.org_id = @orgId
              AND (
                r.visibility = 'public'
                OR (@userId IS NOT NULL AND EXISTS (SELECT 1 FROM users u WHERE u.id = @userId AND u.is_site_admin))
                OR (@userId IS NOT NULL AND r.visibility = 'internal'
                     AND EXISTS (SELECT 1 FROM org_members m WHERE m.org_id = r.org_id AND m.user_id = @userId))
                OR (@userId IS NOT NULL
                     AND EXISTS (SELECT 1 FROM org_members m WHERE m.org_id = r.org_id AND m.user_id = @userId AND m.role IN ('owner','admin')))
                OR (@userId IS NOT NULL AND EXISTS (SELECT 1 FROM repo_collaborators c WHERE c.repo_id = r.id AND c.user_id = @userId))
                OR (@userId IS NOT NULL AND EXISTS (
                     SELECT 1 FROM repo_team_access rta JOIN team_members tm ON tm.team_id = rta.team_id
                     WHERE rta.repo_id = r.id AND tm.user_id = @userId))
              )
            ORDER BY r.name
            """, new { orgId, userId })).ToList();
    }

    public async Task UpdateSettingsAsync(long repoId, string name, string description, string visibility, bool isArchived, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Repo name is required and must be 100 chars or fewer.");
        if (!Visibilities.Contains(visibility))
            throw new ValidationException("Visibility must be public, internal or private.");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            """
            UPDATE repositories SET name = @name, description = @description, visibility = @visibility,
                   is_archived = @isArchived, updated_at = now()
            WHERE id = @repoId
            """,
            new { repoId, name = name.Trim(), description = description.Trim(), visibility, isArchived });
    }

    public async Task DeleteAsync(long orgId, long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM repositories WHERE id = @repoId", new { repoId });
        storage.Delete(orgId, repoId);
    }

    public async Task RenameSlugAsync(long repoId, string newSlug, CancellationToken ct)
    {
        newSlug = Slug.Normalise(newSlug);
        if (!Slug.IsValid(newSlug))
            throw new ValidationException("Invalid repo slug.");

        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var orgId = await conn.QuerySingleOrDefaultAsync<long?>("SELECT org_id FROM repositories WHERE id = @repoId", new { repoId }, tx)
                ?? throw new NotFoundException("Repository not found.");
            var current = await conn.ExecuteScalarAsync<string>("SELECT slug FROM repositories WHERE id = @repoId", new { repoId }, tx)
                ?? throw new NotFoundException("Repository not found.");
            if (current == newSlug)
                return null;
            if (await conn.ExecuteScalarAsync<bool>(
                    "SELECT EXISTS (SELECT 1 FROM repositories WHERE org_id = @orgId AND slug = @newSlug)", new { orgId, newSlug }, tx))
                throw new ValidationException("A repository with that slug already exists in this org.");

            await conn.ExecuteAsync("UPDATE repositories SET slug = @newSlug, updated_at = now() WHERE id = @repoId", new { repoId, newSlug }, tx);
            await SlugRedirects.RecordRenameAsync(conn, tx, SlugRedirects.RepoScope(orgId), current, newSlug);
            return null;
        }, ct);
    }
}
