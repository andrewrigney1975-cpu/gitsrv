using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using LibGit2Sharp;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;
using Repository = LibGit2Sharp.Repository;

namespace GitSrv.Api.Git;

public sealed record ReleaseAsset(long Id, string Name, long SizeBytes, string ContentType, int Downloads, DateTime CreatedAt);
public sealed record ReleaseView(long Id, string TagName, string TargetSha, string Name, string Body,
    bool IsPrerelease, bool IsDraft, string AuthorUsername, DateTime CreatedAt, IReadOnlyList<ReleaseAsset> Assets);

public sealed class ReleaseService(Db db, GitStorage storage)
{
    private sealed record ReleaseRow(long Id, string TagName, string TargetSha, string Name, string Body,
        bool IsPrerelease, bool IsDraft, long CreatedBy, DateTime CreatedAt);

    private string AssetDir(long orgId, long repoId, long releaseId)
        => Path.Combine(storage.RepositoryRoot, "_assets", orgId.ToString(), repoId.ToString(), releaseId.ToString());

    public async Task<IReadOnlyList<ReleaseView>> ListAsync(long repoId, bool includeDrafts, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var rows = (await conn.QueryAsync<ReleaseRow>($"""
            SELECT id, tag_name AS TagName, target_sha AS TargetSha, name, body, is_prerelease AS IsPrerelease,
                   is_draft AS IsDraft, created_by AS CreatedBy, created_at AS CreatedAt
            FROM releases WHERE repo_id = @repoId {(includeDrafts ? "" : "AND NOT is_draft")}
            ORDER BY created_at DESC
            """, new { repoId })).ToList();
        var result = new List<ReleaseView>();
        foreach (var r in rows) result.Add(await HydrateAsync(conn, r));
        return result;
    }

    public async Task<ReleaseView> GetAsync(long repoId, string tag, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync<ReleaseRow>("""
            SELECT id, tag_name AS TagName, target_sha AS TargetSha, name, body, is_prerelease AS IsPrerelease,
                   is_draft AS IsDraft, created_by AS CreatedBy, created_at AS CreatedAt
            FROM releases WHERE repo_id = @repoId AND tag_name = @tag
            """, new { repoId, tag }) ?? throw new NotFoundException("Release not found.");
        return await HydrateAsync(conn, r);
    }

    public async Task<long> CreateAsync(long orgId, long repoId, long userId, string tag, string target, string name,
        string body, bool prerelease, bool draft, string username, string email, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ValidationException("A tag name is required.");

        string targetSha;
        using (var repo = new Repository(storage.RepoPath(orgId, repoId)))
        {
            var existing = repo.Tags[tag];
            if (existing is not null)
            {
                targetSha = existing.Target.Sha;
            }
            else
            {
                var commit = repo.Lookup<Commit>(target) ?? repo.Branches[target]?.Tip
                    ?? throw new ValidationException($"No commit or branch '{target}' to tag.");
                targetSha = commit.Sha;
                if (!draft)
                {
                    var sig = new Signature(username, string.IsNullOrWhiteSpace(email) ? $"{username}@users.noreply.gitsrv" : email, DateTimeOffset.Now);
                    repo.Tags.Add(tag, commit, sig, string.IsNullOrWhiteSpace(body) ? name : body);
                }
            }
        }

        await using var conn = await db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<long>("""
                INSERT INTO releases (repo_id, tag_name, target_sha, name, body, is_prerelease, is_draft, created_by)
                VALUES (@repoId, @tag, @targetSha, @name, @body, @prerelease, @draft, @userId) RETURNING id
                """, new { repoId, tag = tag.Trim(), targetSha, name = name?.Trim() ?? "", body = body?.Trim() ?? "", prerelease, draft, userId });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505")
        {
            throw new ValidationException("A release for that tag already exists.");
        }
    }

    public async Task DeleteAsync(long orgId, long repoId, string tag, bool deleteTag, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM releases WHERE repo_id = @repoId AND tag_name = @tag", new { repoId, tag });
        if (row is null) throw new NotFoundException("Release not found.");
        await conn.ExecuteAsync("DELETE FROM releases WHERE id = @id", new { id = row });
        var dir = AssetDir(orgId, repoId, row.Value);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        if (deleteTag)
        {
            using var repo = new Repository(storage.RepoPath(orgId, repoId));
            if (repo.Tags[tag] is not null) repo.Tags.Remove(tag);
        }
    }

    public async Task<ReleaseAsset> AddAssetAsync(long orgId, long repoId, string tag, string fileName, string contentType,
        Stream content, long maxBytes, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var releaseId = await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM releases WHERE repo_id = @repoId AND tag_name = @tag", new { repoId, tag })
            ?? throw new NotFoundException("Release not found.");

        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe)) throw new ValidationException("Invalid file name.");
        var dir = AssetDir(orgId, repoId, releaseId);
        Directory.CreateDirectory(dir);
        var full = Path.Combine(dir, safe);

        long written;
        await using (var fs = File.Create(full))
        {
            await content.CopyToAsync(fs, ct);
            written = fs.Length;
        }
        if (maxBytes > 0 && written > maxBytes)
        {
            File.Delete(full);
            throw new ValidationException($"Asset exceeds the {maxBytes / (1024 * 1024)} MiB limit.");
        }

        var id = await conn.ExecuteScalarAsync<long>("""
            INSERT INTO release_assets (release_id, name, size_bytes, content_type, storage_path)
            VALUES (@releaseId, @safe, @written, @contentType, @path)
            ON CONFLICT (release_id, name) DO UPDATE SET size_bytes = EXCLUDED.size_bytes, content_type = EXCLUDED.content_type
            RETURNING id
            """, new { releaseId, safe, written, contentType, path = full });
        return new ReleaseAsset(id, safe, written, contentType, 0, DateTime.UtcNow);
    }

    public async Task<(Stream Stream, string Name, string ContentType)> OpenAssetAsync(long repoId, string tag, long assetId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<AssetRow>("""
            SELECT ra.name, ra.content_type AS ContentType, ra.storage_path AS StoragePath
            FROM release_assets ra JOIN releases r ON r.id = ra.release_id
            WHERE ra.id = @assetId AND r.repo_id = @repoId AND r.tag_name = @tag
            """, new { assetId, repoId, tag }) ?? throw new NotFoundException("Asset not found.");
        await conn.ExecuteAsync("UPDATE release_assets SET downloads = downloads + 1 WHERE id = @assetId", new { assetId });
        if (!File.Exists(row.StoragePath)) throw new NotFoundException("Asset file missing.");
        return (File.OpenRead(row.StoragePath), row.Name, row.ContentType);
    }

    private sealed record AssetRow(string Name, string ContentType, string StoragePath);

    private static async Task<ReleaseView> HydrateAsync(System.Data.IDbConnection conn, ReleaseRow r)
    {
        var author = await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @id", new { id = r.CreatedBy });
        var assets = (await conn.QueryAsync<ReleaseAsset>("""
            SELECT id, name, size_bytes AS SizeBytes, content_type AS ContentType, downloads, created_at AS CreatedAt
            FROM release_assets WHERE release_id = @id ORDER BY name
            """, new { r.Id })).ToList();
        return new ReleaseView(r.Id, r.TagName, r.TargetSha, r.Name, r.Body, r.IsPrerelease, r.IsDraft, author!, r.CreatedAt, assets);
    }
}
