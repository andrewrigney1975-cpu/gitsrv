using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;

namespace GitSrv.Api.Identity;

public sealed record OrgSummary(long Id, string Slug, string Name, string Role);
public sealed record OrgMember(long UserId, string Username, string DisplayName, string Role, DateTime AddedAt);

public sealed class OrgService(Db db)
{
    public async Task<Organisation> CreateAsync(long ownerUserId, string slug, string name, string description, CancellationToken ct)
    {
        slug = Slug.Normalise(slug);
        if (!Slug.IsValid(slug))
            throw new ValidationException("Org slug must be 1–40 chars, lowercase letters/digits with single - or _ separators, and not reserved.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Org name is required and must be 100 chars or fewer.");

        return await db.InTransactionAsync(async (conn, tx) =>
        {
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM organisations WHERE slug = @slug)", new { slug }, tx))
                throw new ValidationException("That org slug is taken.");

            var id = await conn.ExecuteScalarAsync<long>(
                """
                INSERT INTO organisations (slug, name, description, created_by)
                VALUES (@slug, @name, @description, @ownerUserId) RETURNING id
                """,
                new { slug, name = name.Trim(), description = description.Trim(), ownerUserId }, tx);

            await conn.ExecuteAsync(
                "INSERT INTO org_members (org_id, user_id, role) VALUES (@id, @ownerUserId, 'owner')",
                new { id, ownerUserId }, tx);

            return new Organisation(id, slug, name.Trim(), description.Trim(), ownerUserId, DateTime.UtcNow);
        }, ct);
    }

    public async Task<IReadOnlyList<OrgSummary>> ListForUserAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgSummary>(
            """
            SELECT o.id, o.slug, o.name, m.role
            FROM organisations o
            JOIN org_members m ON m.org_id = o.id
            WHERE m.user_id = @userId
            ORDER BY o.name
            """, new { userId })).ToList();
    }

    public async Task<Organisation?> GetBySlugAsync(string slug, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Organisation>(
            "SELECT id, slug, name, description, created_by, created_at FROM organisations WHERE slug = @slug",
            new { slug = Slug.Normalise(slug) });
    }

    public async Task<string?> ResolveRedirectAsync(string oldSlug, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await SlugRedirects.ResolveAsync(conn, SlugRedirects.OrgScope, Slug.Normalise(oldSlug));
    }

    public async Task UpdateAsync(long orgId, string name, string description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Org name is required and must be 100 chars or fewer.");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE organisations SET name = @name, description = @description, updated_at = now() WHERE id = @orgId",
            new { orgId, name = name.Trim(), description = description.Trim() });
    }

    public async Task RenameSlugAsync(long orgId, string newSlug, CancellationToken ct)
    {
        newSlug = Slug.Normalise(newSlug);
        if (!Slug.IsValid(newSlug))
            throw new ValidationException("Invalid org slug.");

        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var current = await conn.QuerySingleOrDefaultAsync<string?>(
                "SELECT slug FROM organisations WHERE id = @orgId", new { orgId }, tx)
                ?? throw new NotFoundException("Organisation not found.");
            if (current == newSlug)
                return null;
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM organisations WHERE slug = @newSlug)", new { newSlug }, tx))
                throw new ValidationException("That org slug is taken.");

            await conn.ExecuteAsync("UPDATE organisations SET slug = @newSlug, updated_at = now() WHERE id = @orgId", new { orgId, newSlug }, tx);
            await SlugRedirects.RecordRenameAsync(conn, tx, SlugRedirects.OrgScope, current, newSlug);
            return null;
        }, ct);
    }

    public async Task<IReadOnlyList<OrgMember>> ListMembersAsync(long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<OrgMember>(
            """
            SELECT u.id AS UserId, u.username, u.display_name AS DisplayName, m.role, m.added_at AS AddedAt
            FROM org_members m JOIN users u ON u.id = m.user_id
            WHERE m.org_id = @orgId
            ORDER BY CASE m.role WHEN 'owner' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, u.username
            """, new { orgId })).ToList();
    }

    public async Task AddMemberAsync(long orgId, string username, string role, CancellationToken ct)
    {
        ValidateRole(role);
        await using var conn = await db.OpenAsync(ct);
        var userId = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM users WHERE username = @username", new { username = Slug.Normalise(username) })
            ?? throw new ValidationException($"No user '{username}'.");
        await conn.ExecuteAsync(
            """
            INSERT INTO org_members (org_id, user_id, role) VALUES (@orgId, @userId, @role)
            ON CONFLICT (org_id, user_id) DO UPDATE SET role = EXCLUDED.role
            """, new { orgId, userId, role });
    }

    public async Task SetMemberRoleAsync(long orgId, long userId, string role, CancellationToken ct)
    {
        ValidateRole(role);
        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var affected = await conn.ExecuteAsync(
                "UPDATE org_members SET role = @role WHERE org_id = @orgId AND user_id = @userId",
                new { orgId, userId, role }, tx);
            if (affected == 0)
                throw new NotFoundException("That user is not a member of this org.");
            await GuardLastOwnerAsync(conn, tx, orgId);
            return null;
        }, ct);
    }

    public async Task RemoveMemberAsync(long orgId, long userId, CancellationToken ct)
    {
        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            await conn.ExecuteAsync("DELETE FROM org_members WHERE org_id = @orgId AND user_id = @userId", new { orgId, userId }, tx);
            await GuardLastOwnerAsync(conn, tx, orgId);
            return null;
        }, ct);
    }

    private static async Task GuardLastOwnerAsync(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, long orgId)
    {
        var owners = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM org_members WHERE org_id = @orgId AND role = 'owner'", new { orgId }, tx);
        if (owners == 0)
            throw new ValidationException("An organisation must keep at least one owner.");
    }

    private static void ValidateRole(string role)
    {
        if (role is not ("owner" or "admin" or "member"))
            throw new ValidationException("Role must be owner, admin or member.");
    }
}
