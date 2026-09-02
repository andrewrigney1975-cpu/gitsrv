using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Domain;

namespace GitSrv.Api.Identity;

public sealed record TeamSummary(long Id, string Slug, string Name, int MemberCount);
public sealed record TeamMemberRow(long UserId, string Username, string DisplayName, DateTime AddedAt);

public sealed class TeamService(Db db)
{
    public async Task<Team> CreateAsync(long orgId, string slug, string name, string description, CancellationToken ct)
    {
        slug = Slug.Normalise(slug);
        if (!Slug.IsValid(slug))
            throw new ValidationException("Team slug must be 1–40 chars, lowercase letters/digits with single - or _ separators, and not reserved.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Team name is required and must be 100 chars or fewer.");

        await using var conn = await db.OpenAsync(ct);
        if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM teams WHERE org_id = @orgId AND slug = @slug)", new { orgId, slug }))
            throw new ValidationException("A team with that slug already exists in this org.");

        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO teams (org_id, slug, name, description) VALUES (@orgId, @slug, @name, @description) RETURNING id",
            new { orgId, slug, name = name.Trim(), description = description.Trim() });
        return new Team(id, orgId, slug, name.Trim(), description.Trim(), DateTime.UtcNow);
    }

    public async Task<IReadOnlyList<TeamSummary>> ListAsync(long orgId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<TeamSummary>(
            """
            SELECT t.id, t.slug, t.name,
                   (SELECT count(*) FROM team_members tm WHERE tm.team_id = t.id)::int AS MemberCount
            FROM teams t WHERE t.org_id = @orgId ORDER BY t.name
            """, new { orgId })).ToList();
    }

    public async Task<Team?> GetAsync(long orgId, string slug, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<Team>(
            "SELECT id, org_id, slug, name, description, created_at FROM teams WHERE org_id = @orgId AND slug = @slug",
            new { orgId, slug = Slug.Normalise(slug) });
    }

    public async Task UpdateAsync(long teamId, string name, string description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new ValidationException("Team name is required and must be 100 chars or fewer.");
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE teams SET name = @name, description = @description WHERE id = @teamId",
            new { teamId, name = name.Trim(), description = description.Trim() });
    }

    public async Task DeleteAsync(long teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM teams WHERE id = @teamId", new { teamId });
    }

    public async Task<IReadOnlyList<TeamMemberRow>> ListMembersAsync(long teamId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<TeamMemberRow>(
            """
            SELECT u.id AS UserId, u.username, u.display_name AS DisplayName, tm.added_at AS AddedAt
            FROM team_members tm JOIN users u ON u.id = tm.user_id
            WHERE tm.team_id = @teamId ORDER BY u.username
            """, new { teamId })).ToList();
    }

    /// <summary>Adds a user to a team. The user must already be a member of the team's org.</summary>
    public async Task AddMemberAsync(long orgId, long teamId, string username, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var userId = await conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM users WHERE username = @username", new { username = Slug.Normalise(username) })
            ?? throw new ValidationException($"No user '{username}'.");
        var inOrg = await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM org_members WHERE org_id = @orgId AND user_id = @userId)", new { orgId, userId });
        if (!inOrg)
            throw new ValidationException("Add the user to the organisation before adding them to a team.");
        await conn.ExecuteAsync(
            "INSERT INTO team_members (team_id, user_id) VALUES (@teamId, @userId) ON CONFLICT DO NOTHING",
            new { teamId, userId });
    }

    public async Task RemoveMemberAsync(long teamId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM team_members WHERE team_id = @teamId AND user_id = @userId", new { teamId, userId });
    }
}
