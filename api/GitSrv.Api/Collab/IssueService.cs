using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Data;
using GitSrv.Api.Git;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Collab;

public sealed record Label(long Id, string Name, string Color, string Description);
public sealed record Milestone(long Id, string Title, string Description, DateOnly? DueOn, string State, int OpenIssues, int ClosedIssues);
public sealed record IssueListItem(int Number, string Title, string State, string AuthorUsername, DateTime CreatedAt,
    DateTime UpdatedAt, int Comments, IReadOnlyList<Label> Labels, IReadOnlyList<string> Assignees, string? Milestone);
public sealed record IssueComment(long Id, string AuthorUsername, string Body, string BodyHtml, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record IssueEvent(string Kind, string? ActorUsername, string Detail, DateTime CreatedAt);
public sealed record IssueRefLink(string SourceKind, string SourceRef, bool Closes);
public sealed record IssueDetail(int Number, string Title, string Body, string BodyHtml, string State,
    string AuthorUsername, string? ClosedByUsername, DateTime CreatedAt, DateTime? ClosedAt,
    IReadOnlyList<Label> Labels, IReadOnlyList<string> Assignees, Milestone? Milestone,
    IReadOnlyList<IssueComment> Comments, IReadOnlyList<IssueEvent> Events, IReadOnlyList<IssueRefLink> References);

public sealed class IssueService(Db db, NotificationService notify, ActivityService activity)
{
    private sealed record IssueRow(long Id, int Number, string Title, string Body, string State, long? MilestoneId,
        long CreatedBy, long? ClosedBy, DateTime CreatedAt, DateTime? ClosedAt);
    private sealed record ListRow(long Id, int Number, string Title, string State, string AuthorUsername,
        DateTime CreatedAt, DateTime UpdatedAt, int Comments, string? Milestone);
    private sealed record CommentRow(long Id, string AuthorUsername, string Body, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record LabelWithIssue(long IssueId, long Id, string Name, string Color, string Description);
    private sealed record AssigneeWithIssue(long IssueId, string Username);

    // ---- issues ----

    public async Task<int> CreateAsync(long repoId, string orgSlug, string repoSlug, long userId, string username,
        string title, string body, long[] labelIds, string[] assigneeUsernames, long? milestoneId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("A title is required.");

        var (number, issueId) = await db.InTransactionAsync(async (conn, tx) =>
        {
            var n = await RepoNumbers.NextAsync(conn, tx, repoId);
            var id = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO issues (repo_id, number, title, body, milestone_id, created_by)
                VALUES (@repoId, @n, @title, @body, @milestoneId, @userId) RETURNING id
                """, new { repoId, n, title = title.Trim(), body = body?.Trim() ?? "", milestoneId, userId }, tx);

            foreach (var lid in labelIds ?? [])
                await conn.ExecuteAsync("INSERT INTO issue_labels (issue_id, label_id) SELECT @id, @lid WHERE EXISTS (SELECT 1 FROM labels WHERE id = @lid AND repo_id = @repoId) ON CONFLICT DO NOTHING", new { id, lid, repoId }, tx);

            await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind) VALUES (@id, @userId, 'opened')", new { id, userId }, tx);
            return (n, id);
        }, ct);

        // assignees
        var assignees = await AddAssigneesInternalAsync(repoId, issueId, number, userId, username, assigneeUsernames ?? [], ct);

        await notify.EnsureWatchAsync(repoId, userId, "auto", ct);
        await activity.RecordAsync(userId, null, repoId, "issue_opened", number, $"{username} opened issue #{number}: {title.Trim()}", ct);

        var url = $"#/o/{orgSlug}/{repoSlug}/issues/{number}";
        var mentioned = await notify.ResolveMentionsAsync($"{title} {body}", ct);
        await notify.NotifyAsync(mentioned, userId, repoId, "issue", number, title.Trim(), "mention", body ?? "", url, ct);
        await notify.NotifyAsync(assignees, userId, repoId, "issue", number, title.Trim(), "assign", "", url, ct);

        return number;
    }

    public async Task<IReadOnlyList<IssueListItem>> ListAsync(long repoId, string state, string? label, string? assignee, long? milestoneId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var filters = state switch { "closed" => "AND i.state = 'closed'", "all" => "", _ => "AND i.state = 'open'" };
        if (label is not null) filters += " AND EXISTS (SELECT 1 FROM issue_labels il JOIN labels l ON l.id = il.label_id WHERE il.issue_id = i.id AND l.name = @label)";
        if (assignee is not null) filters += " AND EXISTS (SELECT 1 FROM issue_assignees ia JOIN users u2 ON u2.id = ia.user_id WHERE ia.issue_id = i.id AND u2.username = @assignee)";
        if (milestoneId is not null) filters += " AND i.milestone_id = @milestoneId";

        var rows = (await conn.QueryAsync<ListRow>($"""
            SELECT i.id, i.number, i.title, i.state, u.username AS AuthorUsername, i.created_at AS CreatedAt, i.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM issue_comments c WHERE c.issue_id = i.id)::int AS Comments,
                   m.title AS Milestone
            FROM issues i JOIN users u ON u.id = i.created_by
            LEFT JOIN milestones m ON m.id = i.milestone_id
            WHERE i.repo_id = @repoId {filters}
            ORDER BY i.number DESC
            """, new { repoId, label, assignee, milestoneId })).ToList();

        if (rows.Count == 0) return [];
        var ids = rows.Select(r => r.Id).ToArray();

        var labelRows = (await conn.QueryAsync<LabelWithIssue>(
            "SELECT il.issue_id AS IssueId, l.id, l.name, l.color, l.description FROM issue_labels il JOIN labels l ON l.id = il.label_id WHERE il.issue_id = ANY(@ids)",
            new { ids })).ToLookup(x => x.IssueId, x => new Label(x.Id, x.Name, x.Color, x.Description));
        var assigneeRows = (await conn.QueryAsync<AssigneeWithIssue>(
            "SELECT ia.issue_id AS IssueId, u.username FROM issue_assignees ia JOIN users u ON u.id = ia.user_id WHERE ia.issue_id = ANY(@ids)",
            new { ids })).ToLookup(x => x.IssueId, x => x.Username);

        return rows.Select(r => new IssueListItem(r.Number, r.Title, r.State, r.AuthorUsername, r.CreatedAt, r.UpdatedAt, r.Comments,
            labelRows[r.Id].ToList(), assigneeRows[r.Id].ToList(), r.Milestone)).ToList();
    }

    public async Task<IssueDetail> GetAsync(long repoId, string orgSlug, string repoSlug, int number, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);

        var author = await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @id", new { id = i.CreatedBy });
        var closedBy = i.ClosedBy is null ? null : await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @id", new { id = i.ClosedBy });
        var labels = (await conn.QueryAsync<Label>("SELECT l.id, l.name, l.color, l.description FROM issue_labels il JOIN labels l ON l.id = il.label_id WHERE il.issue_id = @id", new { i.Id })).ToList();
        var assignees = (await conn.QueryAsync<string>("SELECT u.username FROM issue_assignees ia JOIN users u ON u.id = ia.user_id WHERE ia.issue_id = @id ORDER BY u.username", new { i.Id })).ToList();
        Milestone? milestone = i.MilestoneId is null ? null : await LoadMilestoneAsync(conn, i.MilestoneId.Value);

        var comments = (await conn.QueryAsync<CommentRow>("""
            SELECT c.id, u.username AS AuthorUsername, c.body, c.created_at AS CreatedAt, c.updated_at AS UpdatedAt
            FROM issue_comments c JOIN users u ON u.id = c.user_id WHERE c.issue_id = @id ORDER BY c.created_at
            """, new { i.Id }))
            .Select(c => new IssueComment(c.Id, c.AuthorUsername, c.Body, MarkdownRenderer.ToCommentHtml(c.Body, orgSlug, repoSlug), c.CreatedAt, c.UpdatedAt))
            .ToList();

        var events = (await conn.QueryAsync<IssueEvent>("""
            SELECT e.kind, u.username AS ActorUsername, e.detail, e.created_at AS CreatedAt
            FROM issue_events e LEFT JOIN users u ON u.id = e.actor_id WHERE e.issue_id = @id ORDER BY e.created_at
            """, new { i.Id })).ToList();

        var refs = (await conn.QueryAsync<IssueRefLink>("SELECT source_kind AS SourceKind, source_ref AS SourceRef, closes FROM issue_references WHERE issue_id = @id ORDER BY created_at", new { i.Id })).ToList();

        return new IssueDetail(i.Number, i.Title, i.Body, MarkdownRenderer.ToCommentHtml(i.Body, orgSlug, repoSlug), i.State,
            author!, closedBy, i.CreatedAt, i.ClosedAt, labels, assignees, milestone, comments, events, refs);
    }

    public async Task UpdateAsync(long repoId, int number, long userId, string? title, string? body, long? milestoneId, bool clearMilestone, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);
        await conn.ExecuteAsync("""
            UPDATE issues SET title = COALESCE(@title, title), body = COALESCE(@body, body),
                milestone_id = CASE WHEN @clear THEN NULL ELSE COALESCE(@milestoneId, milestone_id) END, updated_at = now()
            WHERE id = @id
            """, new { id = i.Id, title = title?.Trim(), body = body?.Trim(), milestoneId, clear = clearMilestone });
        if (milestoneId is not null || clearMilestone)
            await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind, detail) VALUES (@id, @userId, 'milestoned', @d)",
                new { id = i.Id, userId, d = clearMilestone ? "removed" : "set" });
    }

    public async Task SetStateAsync(long repoId, string orgSlug, string repoSlug, int number, long userId, string username, string state, CancellationToken ct)
    {
        if (state is not ("open" or "closed")) throw new ValidationException("State must be open or closed.");
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);
        if (i.State == state) return;
        await conn.ExecuteAsync("""
            UPDATE issues SET state = @state, closed_by = CASE WHEN @state = 'closed' THEN @userId END,
                closed_at = CASE WHEN @state = 'closed' THEN now() END, updated_at = now()
            WHERE id = @id
            """, new { id = i.Id, state, userId });
        await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind) VALUES (@id, @userId, @k)",
            new { id = i.Id, userId, k = state == "closed" ? "closed" : "reopened" });
        await activity.RecordAsync(userId, null, repoId, state == "closed" ? "issue_closed" : "issue_reopened", number,
            $"{username} {(state == "closed" ? "closed" : "reopened")} issue #{number}", ct);

        var participants = await ParticipantsAsync(conn, i.Id, i.CreatedBy);
        await notify.NotifyAsync(participants, userId, repoId, "issue", number, i.Title, state == "closed" ? "closed" : "comment",
            "", $"#/o/{orgSlug}/{repoSlug}/issues/{number}", ct);
    }

    public async Task<long> CommentAsync(long repoId, string orgSlug, string repoSlug, int number, long userId, string username, string body, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ValidationException("Comment cannot be empty.");
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);
        var id = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO issue_comments (issue_id, user_id, body) VALUES (@id, @userId, @body) RETURNING id",
            new { id = i.Id, userId, body = body.Trim() });
        await conn.ExecuteAsync("UPDATE issues SET updated_at = now() WHERE id = @id", new { id = i.Id });

        await notify.EnsureWatchAsync(repoId, userId, "auto", ct);
        var url = $"#/o/{orgSlug}/{repoSlug}/issues/{number}";
        var participants = await ParticipantsAsync(conn, i.Id, i.CreatedBy);
        var watchers = await notify.WatchersAsync(repoId, ct);
        var mentioned = await notify.ResolveMentionsAsync(body, ct);
        await notify.NotifyAsync(participants.Concat(watchers), userId, repoId, "issue", number, i.Title, "comment", body.Trim(), url, ct);
        await notify.NotifyAsync(mentioned, userId, repoId, "issue", number, i.Title, "mention", body.Trim(), url, ct);
        return id;
    }

    public async Task EditCommentAsync(long userId, long commentId, string body, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var n = await conn.ExecuteAsync("UPDATE issue_comments SET body = @body, updated_at = now() WHERE id = @commentId AND user_id = @userId",
            new { commentId, userId, body = body.Trim() });
        if (n == 0) throw new ForbiddenException("You can only edit your own comments.");
    }

    public async Task DeleteCommentAsync(long userId, long commentId, bool isRepoAdmin, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var owner = await conn.ExecuteScalarAsync<long?>("SELECT user_id FROM issue_comments WHERE id = @commentId", new { commentId });
        if (owner is null) throw new NotFoundException("Comment not found.");
        if (owner != userId && !isRepoAdmin) throw new ForbiddenException();
        await conn.ExecuteAsync("DELETE FROM issue_comments WHERE id = @commentId", new { commentId });
    }

    // ---- labels & assignees ----

    public async Task SetLabelsAsync(long repoId, int number, long userId, long[] labelIds, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);
        await conn.ExecuteAsync("DELETE FROM issue_labels WHERE issue_id = @id", new { i.Id });
        foreach (var lid in labelIds.Distinct())
            await conn.ExecuteAsync("INSERT INTO issue_labels (issue_id, label_id) SELECT @id, @lid WHERE EXISTS (SELECT 1 FROM labels WHERE id = @lid AND repo_id = @repoId)", new { id = i.Id, lid, repoId });
        await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind) VALUES (@id, @userId, 'labeled')", new { id = i.Id, userId });
    }

    public async Task SetAssigneesAsync(long repoId, string orgSlug, string repoSlug, int number, long userId, string username, string[] usernames, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var i = await LoadAsync(conn, repoId, number, null);
        await conn.ExecuteAsync("DELETE FROM issue_assignees WHERE issue_id = @id", new { i.Id });
        var added = await AddAssigneesInternalAsync(repoId, i.Id, number, userId, username, usernames, ct);
        await notify.NotifyAsync(added, userId, repoId, "issue", number, i.Title, "assign", "", $"#/o/{orgSlug}/{repoSlug}/issues/{number}", ct);
    }

    private async Task<IReadOnlyList<long>> AddAssigneesInternalAsync(long repoId, long issueId, int number, long actorId, string actor, string[] usernames, CancellationToken ct)
    {
        if (usernames.Length == 0) return [];
        await using var conn = await db.OpenAsync(ct);
        var ids = (await conn.QueryAsync<long>("SELECT id FROM users WHERE username = ANY(@names)", new { names = usernames.Select(u => u.Trim().ToLowerInvariant()).ToArray() })).ToList();
        foreach (var uid in ids)
        {
            await conn.ExecuteAsync("INSERT INTO issue_assignees (issue_id, user_id) VALUES (@issueId, @uid) ON CONFLICT DO NOTHING", new { issueId, uid });
            await notify.EnsureWatchAsync(repoId, uid, "auto", ct);
        }
        if (ids.Count > 0)
            await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind) VALUES (@issueId, @actorId, 'assigned')", new { issueId, actorId });
        return ids;
    }

    // ---- cross references (called from PR merge) ----

    public async Task LinkAndMaybeCloseAsync(long repoId, string orgSlug, string repoSlug, string sourceKind, string sourceRef,
        string text, bool applyClosings, long actorId, string actorName, CancellationToken ct)
    {
        var refs = TextRefs.IssueRefs(text);
        if (refs.Count == 0) return;
        await using var conn = await db.OpenAsync(ct);
        foreach (var r in refs)
        {
            var issue = await conn.QuerySingleOrDefaultAsync<IssueRow>(SelectIssue + " WHERE repo_id = @repoId AND number = @n", new { repoId, n = r.Number });
            if (issue is null) continue;
            await conn.ExecuteAsync("""
                INSERT INTO issue_references (issue_id, source_kind, source_ref, closes) VALUES (@id, @sourceKind, @sourceRef, @closes)
                ON CONFLICT (issue_id, source_kind, source_ref) DO UPDATE SET closes = EXCLUDED.closes
                """, new { id = issue.Id, sourceKind, sourceRef, closes = r.Closes });
            await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind, detail) VALUES (@id, @actorId, 'referenced', @d)",
                new { id = issue.Id, actorId, d = $"{sourceKind} {sourceRef}" });

            if (r.Closes && applyClosings && issue.State == "open")
            {
                await conn.ExecuteAsync("UPDATE issues SET state = 'closed', closed_by = @actorId, closed_at = now(), updated_at = now() WHERE id = @id", new { id = issue.Id, actorId });
                await conn.ExecuteAsync("INSERT INTO issue_events (issue_id, actor_id, kind, detail) VALUES (@id, @actorId, 'closed', @d)",
                    new { id = issue.Id, actorId, d = $"via {sourceKind} {sourceRef}" });
                await activity.RecordAsync(actorId, null, repoId, "issue_closed", r.Number, $"{actorName} closed issue #{r.Number} via {sourceKind} {sourceRef}", ct);
                var participants = await ParticipantsAsync(conn, issue.Id, issue.CreatedBy);
                await notify.NotifyAsync(participants, actorId, repoId, "issue", r.Number, issue.Title, "closed", "",
                    $"#/o/{orgSlug}/{repoSlug}/issues/{r.Number}", ct);
            }
        }
    }

    // ---- labels / milestones CRUD ----

    public async Task<IReadOnlyList<Label>> ListLabelsAsync(long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<Label>("SELECT id, name, color, description FROM labels WHERE repo_id = @repoId ORDER BY name", new { repoId })).ToList();
    }

    public async Task<long> CreateLabelAsync(long repoId, string name, string color, string description, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("Label name is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(color ?? "", "^#[0-9a-fA-F]{6}$")) color = "#0c66e4";
        await using var conn = await db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<long>(
                "INSERT INTO labels (repo_id, name, color, description) VALUES (@repoId, @name, @color, @description) RETURNING id",
                new { repoId, name = name.Trim(), color, description = description?.Trim() ?? "" });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505") { throw new ValidationException("A label with that name already exists."); }
    }

    public async Task UpdateLabelAsync(long repoId, long id, string name, string color, string description, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE labels SET name = @name, color = @color, description = @description WHERE id = @id AND repo_id = @repoId",
            new { id, repoId, name = name.Trim(), color, description = description?.Trim() ?? "" });
    }

    public async Task DeleteLabelAsync(long repoId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM labels WHERE id = @id AND repo_id = @repoId", new { id, repoId });
    }

    public async Task<IReadOnlyList<Milestone>> ListMilestonesAsync(long repoId, string state, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var w = state == "all" ? "" : "AND m.state = @state";
        return (await conn.QueryAsync<Milestone>($"""
            SELECT m.id, m.title, m.description, m.due_on AS DueOn, m.state,
                   (SELECT count(*) FROM issues i WHERE i.milestone_id = m.id AND i.state = 'open')::int AS OpenIssues,
                   (SELECT count(*) FROM issues i WHERE i.milestone_id = m.id AND i.state = 'closed')::int AS ClosedIssues
            FROM milestones m WHERE m.repo_id = @repoId {w} ORDER BY m.due_on NULLS LAST, m.title
            """, new { repoId, state })).ToList();
    }

    public async Task<long> CreateMilestoneAsync(long repoId, string title, string description, DateOnly? dueOn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("Milestone title is required.");
        await using var conn = await db.OpenAsync(ct);
        try
        {
            return await conn.ExecuteScalarAsync<long>(
                "INSERT INTO milestones (repo_id, title, description, due_on) VALUES (@repoId, @title, @description, @dueOn) RETURNING id",
                new { repoId, title = title.Trim(), description = description?.Trim() ?? "", dueOn });
        }
        catch (Npgsql.PostgresException e) when (e.SqlState == "23505") { throw new ValidationException("A milestone with that title already exists."); }
    }

    public async Task UpdateMilestoneAsync(long repoId, long id, string title, string description, DateOnly? dueOn, string state, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE milestones SET title = @title, description = @description, due_on = @dueOn, state = @state WHERE id = @id AND repo_id = @repoId",
            new { id, repoId, title = title.Trim(), description = description?.Trim() ?? "", dueOn, state = state is "open" or "closed" ? state : "open" });
    }

    public async Task DeleteMilestoneAsync(long repoId, long id, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("DELETE FROM milestones WHERE id = @id AND repo_id = @repoId", new { id, repoId });
    }

    // ---- helpers ----

    private const string SelectIssue = """
        SELECT id, number, title, body, state, milestone_id AS MilestoneId, created_by AS CreatedBy,
               closed_by AS ClosedBy, created_at AS CreatedAt, closed_at AS ClosedAt
        FROM issues
        """;

    private async Task<IssueRow> LoadAsync(System.Data.IDbConnection conn, long repoId, int number, System.Data.IDbTransaction? tx)
        => await conn.QuerySingleOrDefaultAsync<IssueRow>(SelectIssue + " WHERE repo_id = @repoId AND number = @number", new { repoId, number }, tx)
           ?? throw new NotFoundException("Issue not found.");

    private static async Task<Milestone> LoadMilestoneAsync(System.Data.IDbConnection conn, long id)
        => await conn.QuerySingleAsync<Milestone>("""
            SELECT id, title, description, due_on AS DueOn, state,
                   (SELECT count(*) FROM issues i WHERE i.milestone_id = @id AND i.state = 'open')::int AS OpenIssues,
                   (SELECT count(*) FROM issues i WHERE i.milestone_id = @id AND i.state = 'closed')::int AS ClosedIssues
            FROM milestones WHERE id = @id
            """, new { id });

    private static async Task<IReadOnlyList<long>> ParticipantsAsync(System.Data.IDbConnection conn, long issueId, long authorId)
    {
        var ids = (await conn.QueryAsync<long>("""
            SELECT user_id FROM issue_comments WHERE issue_id = @issueId
            UNION SELECT user_id FROM issue_assignees WHERE issue_id = @issueId
            """, new { issueId })).ToList();
        ids.Add(authorId);
        return ids;
    }
}
