using Dapper;
using GitSrv.Api.Data;
using GitSrv.Api.Git;

namespace GitSrv.Api.Collab;

public sealed record InboxItem(long Id, long? RepoId, string? OrgSlug, string? RepoSlug, string SubjectKind,
    int? SubjectNumber, string Title, string Reason, string Body, string Url, bool IsRead, DateTime CreatedAt);

public sealed class NotificationService(Db db)
{
    /// <summary>Create one notification per recipient (excluding the actor), skipping obvious dupes.</summary>
    public async Task NotifyAsync(IEnumerable<long> recipientIds, long actorId, long repoId, string subjectKind,
        int subjectNumber, string title, string reason, string body, string url, CancellationToken ct)
    {
        var ids = recipientIds.Where(id => id != actorId).Distinct().ToList();
        if (ids.Count == 0) return;

        await using var conn = await db.OpenAsync(ct);
        foreach (var uid in ids)
        {
            await conn.ExecuteAsync("""
                INSERT INTO notifications (user_id, repo_id, subject_kind, subject_number, title, reason, body, url)
                SELECT @uid, @repoId, @subjectKind, @subjectNumber, @title, @reason, @body, @url
                WHERE NOT EXISTS (
                    SELECT 1 FROM notifications n
                    WHERE n.user_id = @uid AND n.repo_id = @repoId AND n.subject_kind = @subjectKind
                      AND n.subject_number = @subjectNumber AND n.reason = @reason AND NOT n.is_read
                      AND n.created_at > now() - interval '2 minutes')
                """,
                new { uid, repoId, subjectKind, subjectNumber, title, reason, body, url });
        }
    }

    public async Task<IReadOnlyList<long>> ResolveMentionsAsync(string text, CancellationToken ct)
    {
        var names = TextRefs.Mentions(text);
        if (names.Count == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<long>("SELECT id FROM users WHERE username = ANY(@names)", new { names = names.ToArray() })).ToList();
    }

    public async Task<IReadOnlyList<long>> WatchersAsync(long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<long>("SELECT user_id FROM repo_watches WHERE repo_id = @repoId", new { repoId })).ToList();
    }

    public async Task EnsureWatchAsync(long repoId, long userId, string reason, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "INSERT INTO repo_watches (repo_id, user_id, reason) VALUES (@repoId, @userId, @reason) ON CONFLICT DO NOTHING",
            new { repoId, userId, reason });
    }

    public async Task SetWatchAsync(long repoId, long userId, bool watching, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        if (watching)
            await conn.ExecuteAsync("INSERT INTO repo_watches (repo_id, user_id, reason) VALUES (@repoId, @userId, 'manual') ON CONFLICT DO NOTHING", new { repoId, userId });
        else
            await conn.ExecuteAsync("DELETE FROM repo_watches WHERE repo_id = @repoId AND user_id = @userId", new { repoId, userId });
    }

    public async Task<bool> IsWatchingAsync(long repoId, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(
            "SELECT EXISTS (SELECT 1 FROM repo_watches WHERE repo_id = @repoId AND user_id = @userId)", new { repoId, userId });
    }

    public async Task<IReadOnlyList<InboxItem>> InboxAsync(long userId, bool unreadOnly, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<InboxItem>($"""
            SELECT n.id, n.repo_id AS RepoId, o.slug AS OrgSlug, r.slug AS RepoSlug, n.subject_kind AS SubjectKind,
                   n.subject_number AS SubjectNumber, n.title, n.reason, n.body, n.url, n.is_read AS IsRead, n.created_at AS CreatedAt
            FROM notifications n
            LEFT JOIN repositories r ON r.id = n.repo_id
            LEFT JOIN organisations o ON o.id = r.org_id
            WHERE n.user_id = @userId {(unreadOnly ? "AND NOT n.is_read" : "")}
            ORDER BY n.created_at DESC LIMIT 100
            """, new { userId })).ToList();
    }

    public async Task<int> UnreadCountAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM notifications WHERE user_id = @userId AND NOT is_read", new { userId });
    }

    public async Task MarkReadAsync(long userId, long[] ids, bool read, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE notifications SET is_read = @read WHERE user_id = @userId AND id = ANY(@ids)", new { userId, ids, read });
    }

    public async Task MarkAllReadAsync(long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE notifications SET is_read = true WHERE user_id = @userId AND NOT is_read", new { userId });
    }
}
