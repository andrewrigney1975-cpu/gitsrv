using System.Security.Cryptography;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Packages;

public sealed record PackageRow(long Id, long OrgId, string Kind, string Name, string Visibility);
public sealed record PackageSummary(long Id, string Kind, string Name, string Visibility, int Versions, long SizeBytes, DateTime UpdatedAt);
public sealed record VersionSummary(long Id, string Version, bool Yanked, DateTime CreatedAt, string? PublishedByUsername);
public sealed record PackageFileRow(long Id, string Name, string Digest, long SizeBytes, string ContentType, string StorageKey);

public sealed class PackageService(Db db, Authorizer authz, PatService pats, AccountService accounts)
{
    // ---- registry auth ----

    public async Task<long?> AuthenticateAsync(HttpContext ctx, CancellationToken ct)
    {
        var header = ctx.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var v = await pats.VerifyAsync(header["Bearer ".Length..].Trim(), ct);
            return v?.UserId;
        }
        if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                var i = decoded.IndexOf(':');
                if (i < 0) return null;
                var (user, secret) = (decoded[..i], decoded[(i + 1)..]);
                if (secret.StartsWith(PatService.Prefix, StringComparison.Ordinal))
                    return (await pats.VerifyAsync(secret, ct))?.UserId;
                return (await accounts.VerifyCredentialsAsync(user, secret, ct))?.Id;
            }
            catch (FormatException) { return null; }
        }
        return null;
    }

    public async Task<long> ResolveOrgAsync(string slug, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<long?>("SELECT id FROM organisations WHERE slug = @slug", new { slug = Identity.Slug.Normalise(slug) })
            ?? throw new NotFoundException("Organisation not found.");
    }

    public async Task RequireOrgMemberAsync(long userId, long orgId, CancellationToken ct)
    {
        var role = await authz.GetOrgRoleAsync(userId, orgId, ct);
        if (role is null && !await authz.IsSiteAdminAsync(userId, ct))
            throw new ForbiddenException("You are not a member of this organisation.");
    }

    /// <summary>Read a package: public → anyone, otherwise org membership.</summary>
    public async Task<PackageRow> ResolveForReadAsync(long orgId, string kind, string name, long? userId, CancellationToken ct)
    {
        var pkg = await GetAsync(orgId, kind, name, ct) ?? throw new NotFoundException("Package not found.");
        if (pkg.Visibility == "public") return pkg;
        if (userId is null) throw new UnauthorizedException();
        await RequireOrgMemberAsync(userId.Value, orgId, ct);
        return pkg;
    }

    public async Task<PackageRow?> GetAsync(long orgId, string kind, string name, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<PackageRow>(
            "SELECT id, org_id AS OrgId, kind, name, visibility FROM packages WHERE org_id = @orgId AND kind = @kind AND name = @name",
            new { orgId, kind, name });
    }

    public async Task<long> EnsurePackageAsync(long orgId, string kind, string name, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO packages (org_id, kind, name, created_by) VALUES (@orgId, @kind, @name, @userId)
            ON CONFLICT (org_id, kind, name) DO UPDATE SET updated_at = now()
            RETURNING id
            """, new { orgId, kind, name, userId });
    }

    public async Task<long> AddVersionAsync(long packageId, string version, string metadataJson, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO package_versions (package_id, version, metadata, published_by) VALUES (@packageId, @version, @metadataJson, @userId)
            ON CONFLICT (package_id, version) DO UPDATE SET metadata = EXCLUDED.metadata
            RETURNING id
            """, new { packageId, version, metadataJson, userId });
    }

    public async Task<long> AddFileAsync(long packageId, long? versionId, string name, string digest, long size,
        string contentType, string storageKey, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE packages SET updated_at = now() WHERE id = @packageId", new { packageId });
        return await conn.ExecuteScalarAsync<long>("""
            INSERT INTO package_files (package_id, version_id, name, digest, size_bytes, content_type, storage_key)
            VALUES (@packageId, @versionId, @name, @digest, @size, @contentType, @storageKey)
            RETURNING id
            """, new { packageId, versionId, name, digest, size, contentType, storageKey });
    }

    public async Task<PackageFileRow?> FindFileAsync(long packageId, string nameOrDigest, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var f = await conn.QuerySingleOrDefaultAsync<PackageFileRow>("""
            SELECT id, name, digest, size_bytes AS SizeBytes, content_type AS ContentType, storage_key AS StorageKey
            FROM package_files WHERE package_id = @packageId AND (name = @k OR digest = @k)
            ORDER BY id DESC LIMIT 1
            """, new { packageId, k = nameOrDigest });
        if (f is not null)
            await conn.ExecuteAsync("UPDATE package_files SET downloads = downloads + 1 WHERE id = @id", new { f.Id });
        return f;
    }

    // ---- browse (UI) ----

    public async Task<IReadOnlyList<PackageSummary>> ListForOrgAsync(long orgId, long? userId, CancellationToken ct)
    {
        var member = userId is not null && (await authz.GetOrgRoleAsync(userId.Value, orgId, ct) is not null || await authz.IsSiteAdminAsync(userId.Value, ct));
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<PackageSummary>($"""
            SELECT p.id, p.kind, p.name, p.visibility,
                   (SELECT count(*) FROM package_versions v WHERE v.package_id = p.id)::int AS Versions,
                   COALESCE((SELECT sum(size_bytes) FROM package_files f WHERE f.package_id = p.id), 0)::bigint AS SizeBytes,
                   p.updated_at AS UpdatedAt
            FROM packages p WHERE p.org_id = @orgId {(member ? "" : "AND p.visibility = 'public'")}
            ORDER BY p.updated_at DESC
            """, new { orgId })).ToList();
    }

    public async Task<IReadOnlyList<VersionSummary>> ListVersionsAsync(long packageId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<VersionSummary>("""
            SELECT v.id, v.version, v.yanked, v.created_at AS CreatedAt, u.username AS PublishedByUsername
            FROM package_versions v LEFT JOIN users u ON u.id = v.published_by
            WHERE v.package_id = @packageId ORDER BY v.created_at DESC
            """, new { packageId })).ToList();
    }

    public async Task SetVisibilityAsync(long orgId, string kind, string name, string visibility, CancellationToken ct)
    {
        if (visibility is not ("public" or "internal" or "private")) throw new ValidationException("Invalid visibility.");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE packages SET visibility = @visibility, updated_at = now() WHERE org_id = @orgId AND kind = @kind AND name = @name",
            new { orgId, kind, name, visibility });
    }

    public async Task DeleteAsync(long packageId, IArtifactStore store, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var keys = (await conn.QueryAsync<string>("SELECT storage_key FROM package_files WHERE package_id = @packageId", new { packageId })).ToList();
        await conn.ExecuteAsync("DELETE FROM packages WHERE id = @packageId", new { packageId });
        foreach (var k in keys) await store.DeleteAsync(k);
    }

    public async Task<long> OrgStorageBytesAsync(long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(
            "SELECT COALESCE(sum(f.size_bytes), 0)::bigint FROM package_files f JOIN packages p ON p.id = f.package_id WHERE p.org_id = @orgId",
            new { orgId });
    }

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
