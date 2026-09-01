using System.Collections.Concurrent;
using Dapper;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Data;
using LibGit2Sharp;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;
using Repository = LibGit2Sharp.Repository;

namespace GitSrv.Api.Git;

// ---- read DTOs ----
public sealed record PrListItem(int Number, string Title, string State, bool IsDraft, string BaseBranch,
    string HeadBranch, string AuthorUsername, DateTime CreatedAt, DateTime UpdatedAt, int Comments);

public sealed record PrComment(long Id, long? ThreadId, long? ReviewId, string AuthorUsername, string Body,
    bool IsPending, DateTime CreatedAt, DateTime UpdatedAt);
public sealed record PrThread(long Id, string FilePath, int? Line, string Side, bool IsResolved,
    string? ResolvedByUsername, IReadOnlyList<PrComment> Comments);
public sealed record PrReview(long Id, string AuthorUsername, string State, string Body, string CommitSha, DateTime CreatedAt);

public sealed record PrDetail(
    int Number, string Title, string Body, string State, bool IsDraft,
    string BaseBranch, string HeadBranch, string HeadSha, string? MergeSha, string? MergeMethod,
    string AuthorUsername, string? MergedByUsername, DateTime CreatedAt, DateTime? MergedAt, DateTime? ClosedAt,
    IReadOnlyList<string> Reviewers, IReadOnlyList<PrReview> Reviews, IReadOnlyList<PrThread> Threads,
    IReadOnlyList<PrComment> Conversation, Comparison? Compare, MergeStatus Merge);

public sealed record MergeStatus(bool Mergeable, bool HasConflicts, IReadOnlyList<string> ConflictPaths,
    bool BlockedByReview, bool BlockedByDraft, int Approvals, int ChangesRequested,
    bool AllowMerge, bool AllowSquash, bool AllowRebase);

public sealed class PullRequestService(Db db, Authorizer authz, RepoBrowseService browse, PrMergeService merger,
    Collab.IssueService issues, Collab.ActivityService activity, Collab.NotificationService notify,
    Actions.ActionsService actions, Integrations.EnklrService enklr, IConfiguration config, ILogger<PullRequestService> logger)
{
    // One merge/sync at a time per repo.
    private static readonly ConcurrentDictionary<long, SemaphoreSlim> RepoLocks = new();
    private static SemaphoreSlim Lock(long repoId) => RepoLocks.GetOrAdd(repoId, _ => new SemaphoreSlim(1, 1));

    private sealed record RepoMergeCfg(bool AllowMergeCommit, bool AllowSquash, bool AllowRebase, bool DeleteBranchOnMerge);
    private sealed record OrgRepoSlugs(string OrgSlug, string RepoSlug);
    private sealed record ProtRule(int RequiredApprovals, bool RequireStatusChecks);

    private static string CommitMessages(string repoDir, PrRow pr)
    {
        try
        {
            using var repo = new Repository(repoDir);
            var head = repo.Lookup<Commit>(pr.HeadSha);
            var mb = head is null ? null : repo.ObjectDatabase.FindMergeBase(repo.Branches[pr.BaseBranch]?.Tip ?? head, head);
            if (head is null) return "";
            var filter = new CommitFilter { IncludeReachableFrom = head, ExcludeReachableFrom = mb };
            return string.Join("\n", repo.Commits.QueryBy(filter).Take(100).Select(c => c.Message));
        }
        catch { return ""; }
    }
    private sealed record PrRow(long Id, int Number, string Title, string Body, string State, bool IsDraft,
        string BaseBranch, string HeadBranch, string HeadSha, string? MergeSha, string? MergeMethod,
        long CreatedBy, long? MergedBy, DateTime CreatedAt, DateTime? MergedAt, DateTime? ClosedAt);

    // ---- create ----

    public async Task<int> CreateAsync(long repoId, string orgSlug, string repoSlug, string repoDir, long userId, string username,
        string title, string body, string baseBranch, string headBranch, bool isDraft, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ValidationException("A title is required.");
        baseBranch = baseBranch.Trim();
        headBranch = headBranch.Trim();
        if (baseBranch == headBranch) throw new ValidationException("The base and head branches must differ.");

        string headSha;
        int ahead;
        using (var repo = new Repository(repoDir))
        {
            if (repo.Branches[baseBranch] is null) throw new ValidationException($"No branch '{baseBranch}'.");
            var head = repo.Branches[headBranch] ?? throw new ValidationException($"No branch '{headBranch}'.");
            headSha = head.Tip!.Sha;
            var mb = repo.ObjectDatabase.FindMergeBase(repo.Branches[baseBranch].Tip, head.Tip);
            ahead = mb is null ? 1 : repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = head.Tip, ExcludeReachableFrom = mb }).Count();
        }
        if (ahead == 0) throw new ValidationException("There is nothing to compare — the head branch has no commits the base is missing.");

        var number = await db.InTransactionAsync(async (conn, tx) =>
        {
            var existing = await conn.QuerySingleOrDefaultAsync<int?>(
                "SELECT number FROM pull_requests WHERE repo_id = @repoId AND head_branch = @headBranch AND base_branch = @baseBranch AND state = 'open'",
                new { repoId, headBranch, baseBranch }, tx);
            if (existing is not null) throw new ValidationException($"PR #{existing} is already open for {headBranch} → {baseBranch}.");

            var n = await Data.RepoNumbers.NextAsync(conn, tx, repoId);
            await conn.ExecuteAsync(
                """
                INSERT INTO pull_requests (repo_id, number, title, body, is_draft, base_branch, head_branch, head_sha, created_by)
                VALUES (@repoId, @n, @title, @body, @isDraft, @baseBranch, @headBranch, @headSha, @userId)
                """,
                new { repoId, n, title = title.Trim(), body = body?.Trim() ?? "", isDraft, baseBranch, headBranch, headSha, userId }, tx);
            return n;
        }, ct);

        await activity.RecordAsync(userId, null, repoId, "pr_opened", number, $"{username} opened PR #{number}: {title.Trim()}", ct);

        // pull_request workflows
        var org = await browse.ResolveAsync(orgSlug, repoSlug, userId, ct);
        var baseUrl = config["App:PublicBaseUrl"] ?? "http://localhost:8080";
        await actions.DispatchAsync(org.OrgId, orgSlug, repoSlug, repoId,
            baseUrl, "pull_request", $"refs/heads/{baseBranch}", headSha, number, userId, ct);

        await enklr.LinkAndNotifyAsync(org.OrgId, repoId, "pull", $"#{number}", title.Trim(), "open",
            $"{baseUrl}/#/o/{orgSlug}/{repoSlug}/pulls/{number}", $"{title} {body} {headBranch}", username, "pr_opened", ct);

        await notify.EnsureWatchAsync(repoId, userId, "auto", ct);
        var mentioned = await notify.ResolveMentionsAsync($"{title} {body}", ct);
        await notify.NotifyAsync(mentioned, userId, repoId, "pull", number, title.Trim(), "mention", body ?? "", $"#/o/{orgSlug}/{repoSlug}/pulls/{number}", ct);
        await issues.LinkAndMaybeCloseAsync(repoId, orgSlug, repoSlug, "pr", $"#{number}", $"{title} {body}", applyClosings: false, userId, username, ct);
        return number;
    }

    // ---- read ----

    public async Task<IReadOnlyList<PrListItem>> ListAsync(long repoId, string state, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var where = state switch
        {
            "closed" => "AND p.state IN ('closed','merged')",
            "all" => "",
            _ => "AND p.state = 'open'",
        };
        return (await conn.QueryAsync<PrListItem>($"""
            SELECT p.number, p.title, p.state, p.is_draft AS IsDraft, p.base_branch AS BaseBranch, p.head_branch AS HeadBranch,
                   u.username AS AuthorUsername, p.created_at AS CreatedAt, p.updated_at AS UpdatedAt,
                   (SELECT count(*) FROM pr_comments c WHERE c.pr_id = p.id AND NOT c.is_pending)::int AS Comments
            FROM pull_requests p JOIN users u ON u.id = p.created_by
            WHERE p.repo_id = @repoId {where}
            ORDER BY p.number DESC
            """, new { repoId })).ToList();
    }

    public async Task<PrDetail> GetAsync(long repoId, string repoDir, int number, long? viewerId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var pr = await LoadRowAsync(conn, repoId, number, null);

        var cfg = await conn.QuerySingleAsync<RepoMergeCfg>(
            "SELECT allow_merge_commit AS AllowMergeCommit, allow_squash AS AllowSquash, allow_rebase AS AllowRebase, delete_branch_on_merge AS DeleteBranchOnMerge FROM repositories WHERE id = @repoId",
            new { repoId });

        var author = await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @id", new { id = pr.CreatedBy });
        var mergedBy = pr.MergedBy is null ? null : await conn.ExecuteScalarAsync<string>("SELECT username FROM users WHERE id = @id", new { id = pr.MergedBy });
        var reviewers = (await conn.QueryAsync<string>(
            "SELECT u.username FROM pr_reviewers r JOIN users u ON u.id = r.user_id WHERE r.pr_id = @id ORDER BY u.username", new { id = pr.Id })).ToList();

        var reviews = (await conn.QueryAsync<PrReview>("""
            SELECT r.id, u.username AS AuthorUsername, r.state, r.body, r.commit_sha AS CommitSha, r.created_at AS CreatedAt
            FROM pr_reviews r JOIN users u ON u.id = r.user_id WHERE r.pr_id = @id ORDER BY r.created_at
            """, new { id = pr.Id })).ToList();

        var comments = (await conn.QueryAsync<PrCommentRow>("""
            SELECT c.id, c.thread_id AS ThreadId, c.review_id AS ReviewId, u.username AS AuthorUsername, c.body,
                   c.is_pending AS IsPending, c.user_id AS UserId, c.created_at AS CreatedAt, c.updated_at AS UpdatedAt
            FROM pr_comments c JOIN users u ON u.id = c.user_id WHERE c.pr_id = @id ORDER BY c.created_at
            """, new { id = pr.Id })).ToList();

        // Pending comments are visible only to their author.
        bool Visible(PrCommentRow c) => !c.IsPending || c.UserId == viewerId;
        PrComment Proj(PrCommentRow c) => new(c.Id, c.ThreadId, c.ReviewId, c.AuthorUsername, c.Body, c.IsPending, c.CreatedAt, c.UpdatedAt);

        var threadRows = (await conn.QueryAsync<PrThreadRow>("""
            SELECT t.id, t.file_path AS FilePath, t.line, t.side, t.is_resolved AS IsResolved,
                   ru.username AS ResolvedByUsername
            FROM pr_threads t LEFT JOIN users ru ON ru.id = t.resolved_by WHERE t.pr_id = @id ORDER BY t.file_path, t.line
            """, new { id = pr.Id })).ToList();

        var threads = threadRows
            .Select(t => new PrThread(t.Id, t.FilePath, t.Line, t.Side, t.IsResolved, t.ResolvedByUsername,
                comments.Where(c => c.ThreadId == t.Id && Visible(c)).Select(Proj).ToList()))
            .Where(t => t.Comments.Count > 0)   // hide threads whose only comments are someone else's pending draft
            .ToList();
        var conversation = comments.Where(c => c.ThreadId is null && Visible(c)).Select(Proj).ToList();

        Comparison? cmp = null;
        MergeStatus mergeStatus;
        if (pr.State == "open")
        {
            try
            {
                using var reader = new RepoReader(repoDir);
                cmp = reader.Compare(pr.BaseBranch, pr.HeadBranch);
            }
            catch (Exception ex) { logger.LogWarning(ex, "PR #{Number} compare failed", number); }

            var changesRequested = reviews
                .GroupBy(r => r.AuthorUsername)
                .Count(g => g.OrderBy(r => r.CreatedAt).Last().State == "request_changes");
            var approvals = reviews
                .GroupBy(r => r.AuthorUsername)
                .Count(g => g.OrderBy(r => r.CreatedAt).Last().State == "approve");

            mergeStatus = new MergeStatus(
                Mergeable: cmp?.Mergeable == true && cmp.Ahead > 0 && !pr.IsDraft && changesRequested == 0,
                HasConflicts: cmp?.Mergeable == false,
                ConflictPaths: cmp?.ConflictPaths ?? [],
                BlockedByReview: changesRequested > 0,
                BlockedByDraft: pr.IsDraft,
                Approvals: approvals, ChangesRequested: changesRequested,
                AllowMerge: cfg.AllowMergeCommit, AllowSquash: cfg.AllowSquash, AllowRebase: cfg.AllowRebase);
        }
        else
        {
            mergeStatus = new MergeStatus(false, false, [], false, false, 0, 0, false, false, false);
        }

        return new PrDetail(pr.Number, pr.Title, pr.Body, pr.State, pr.IsDraft, pr.BaseBranch, pr.HeadBranch,
            pr.HeadSha, pr.MergeSha, pr.MergeMethod, author!, mergedBy, pr.CreatedAt, pr.MergedAt, pr.ClosedAt,
            reviewers, reviews, threads, conversation, cmp, mergeStatus);
    }

    private sealed record PrCommentRow(long Id, long? ThreadId, long? ReviewId, string AuthorUsername, string Body,
        bool IsPending, long UserId, DateTime CreatedAt, DateTime UpdatedAt);
    private sealed record PrThreadRow(long Id, string FilePath, int? Line, string Side, bool IsResolved, string? ResolvedByUsername);

    // ---- mutate: conversation & review ----

    public async Task<long> CommentAsync(long repoId, int number, long userId, string body,
        long? threadId, string? filePath, int? line, string side, bool pending, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body)) throw new ValidationException("Comment cannot be empty.");
        return await db.InTransactionAsync(async (conn, tx) =>
        {
            var pr = await LoadRowAsync(conn, repoId, number, tx);
            long? tId = threadId;
            if (tId is null && filePath is not null)
            {
                tId = await conn.ExecuteScalarAsync<long>(
                    "INSERT INTO pr_threads (pr_id, file_path, line, side) VALUES (@prId, @filePath, @line, @side) RETURNING id",
                    new { prId = pr.Id, filePath, line, side = side is "old" or "new" ? side : "new" }, tx);
            }
            var id = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO pr_comments (pr_id, thread_id, user_id, body, is_pending) VALUES (@prId, @tId, @userId, @body, @pending) RETURNING id",
                new { prId = pr.Id, tId, userId, body = body.Trim(), pending }, tx);
            await Touch(conn, tx, pr.Id);
            return id;
        }, ct);
    }

    public async Task EditCommentAsync(long repoId, int number, long userId, long commentId, string body, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var n = await conn.ExecuteAsync(
            "UPDATE pr_comments SET body = @body, updated_at = now() WHERE id = @commentId AND user_id = @userId",
            new { commentId, userId, body = body.Trim() });
        if (n == 0) throw new ForbiddenException("You can only edit your own comments.");
    }

    public async Task DeleteCommentAsync(long repoId, int number, long userId, long commentId, bool isRepoAdmin, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var owner = await conn.ExecuteScalarAsync<long?>("SELECT user_id FROM pr_comments WHERE id = @commentId", new { commentId });
        if (owner is null) throw new NotFoundException("Comment not found.");
        if (owner != userId && !isRepoAdmin) throw new ForbiddenException();
        await conn.ExecuteAsync("DELETE FROM pr_comments WHERE id = @commentId", new { commentId });
    }

    public async Task ResolveThreadAsync(long repoId, int number, long userId, long threadId, bool resolved, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync(
            "UPDATE pr_threads SET is_resolved = @resolved, resolved_by = CASE WHEN @resolved THEN @userId ELSE NULL END WHERE id = @threadId",
            new { threadId, resolved, userId });
    }

    public async Task SubmitReviewAsync(long repoId, int number, long userId, string state, string body, CancellationToken ct)
    {
        if (state is not ("comment" or "approve" or "request_changes"))
            throw new ValidationException("Invalid review state.");
        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var pr = await LoadRowAsync(conn, repoId, number, tx);
            if (pr.CreatedBy == userId && state != "comment")
                throw new ValidationException("You cannot approve or request changes on your own pull request.");

            var reviewId = await conn.ExecuteScalarAsync<long>(
                "INSERT INTO pr_reviews (pr_id, user_id, state, body, commit_sha) VALUES (@prId, @userId, @state, @body, @sha) RETURNING id",
                new { prId = pr.Id, userId, state, body = body?.Trim() ?? "", sha = pr.HeadSha }, tx);

            // Publish this reviewer's pending comments.
            await conn.ExecuteAsync(
                "UPDATE pr_comments SET is_pending = false, review_id = @reviewId WHERE pr_id = @prId AND user_id = @userId AND is_pending",
                new { reviewId, prId = pr.Id, userId }, tx);
            await Touch(conn, tx, pr.Id);
            return null;
        }, ct);
    }

    public async Task SetReviewersAsync(long repoId, int number, IEnumerable<string> usernames, CancellationToken ct)
    {
        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var pr = await LoadRowAsync(conn, repoId, number, tx);
            await conn.ExecuteAsync("DELETE FROM pr_reviewers WHERE pr_id = @prId", new { prId = pr.Id }, tx);
            foreach (var name in usernames.Select(u => u.Trim().ToLowerInvariant()).Distinct())
            {
                var uid = await conn.ExecuteScalarAsync<long?>("SELECT id FROM users WHERE username = @name", new { name }, tx);
                if (uid is not null)
                    await conn.ExecuteAsync("INSERT INTO pr_reviewers (pr_id, user_id) VALUES (@prId, @uid) ON CONFLICT DO NOTHING",
                        new { prId = pr.Id, uid }, tx);
            }
            return null;
        }, ct);
    }

    public async Task UpdateAsync(long repoId, int number, long userId, string? title, string? body, bool? isDraft, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var pr = await LoadRowAsync(conn, repoId, number, null);
        await conn.ExecuteAsync("""
            UPDATE pull_requests SET title = COALESCE(@title, title), body = COALESCE(@body, body),
                is_draft = COALESCE(@isDraft, is_draft), updated_at = now()
            WHERE id = @id
            """, new { id = pr.Id, title = title?.Trim(), body = body?.Trim(), isDraft });
    }

    public async Task SetStateAsync(long repoId, int number, string state, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE pull_requests SET state = @state, closed_at = CASE WHEN @state = 'closed' THEN now() ELSE NULL END, updated_at = now()
            WHERE repo_id = @repoId AND number = @number AND state <> 'merged'
            """, new { repoId, number, state });
    }

    // ---- merge ----

    public async Task MergeAsync(long repoId, string repoDir, int number, long userId, string username, string email,
        string method, CancellationToken ct)
    {
        var gate = Lock(repoId);
        await gate.WaitAsync(ct);
        try
        {
            PrRow pr;
            RepoMergeCfg cfg;
            await using (var conn = await db.OpenAsync(ct))
            {
                pr = await LoadRowAsync(conn, repoId, number, null);
                cfg = await conn.QuerySingleAsync<RepoMergeCfg>(
                    "SELECT allow_merge_commit AS AllowMergeCommit, allow_squash AS AllowSquash, allow_rebase AS AllowRebase, delete_branch_on_merge AS DeleteBranchOnMerge FROM repositories WHERE id = @repoId",
                    new { repoId });
            }
            if (pr.State != "open") throw new ValidationException("This pull request is not open.");
            if (pr.IsDraft) throw new ValidationException("This pull request is a draft.");
            if ((method == "merge" && !cfg.AllowMergeCommit) || (method == "squash" && !cfg.AllowSquash) || (method == "rebase" && !cfg.AllowRebase))
                throw new ValidationException($"The '{method}' merge method is disabled for this repository.");

            // Outstanding change requests block the merge.
            await using (var conn = await db.OpenAsync(ct))
            {
                var changesRequested = await conn.ExecuteScalarAsync<int>("""
                    SELECT count(*) FROM (
                      SELECT DISTINCT ON (user_id) state FROM pr_reviews WHERE pr_id = @id ORDER BY user_id, created_at DESC
                    ) latest WHERE state = 'request_changes'
                    """, new { id = pr.Id });
                if (changesRequested > 0) throw new ValidationException("Changes have been requested and not yet resolved.");

                var rule = await conn.QuerySingleOrDefaultAsync<ProtRule>("""
                    SELECT required_approvals AS RequiredApprovals, require_status_checks AS RequireStatusChecks
                    FROM branch_protections
                    WHERE repo_id = @repoId AND (@branch = pattern OR (position('*' in pattern) > 0))
                    ORDER BY (pattern = @branch) DESC LIMIT 1
                    """, new { repoId, branch = pr.BaseBranch });
                if (rule is { RequiredApprovals: > 0 })
                {
                    var approvals = await conn.ExecuteScalarAsync<int>("""
                        SELECT count(*) FROM (
                          SELECT DISTINCT ON (user_id) state FROM pr_reviews WHERE pr_id = @id ORDER BY user_id, created_at DESC
                        ) latest WHERE state = 'approve'
                        """, new { id = pr.Id });
                    if (approvals < rule.RequiredApprovals)
                        throw new ValidationException($"This branch requires {rule.RequiredApprovals} approval(s); it has {approvals}.");
                }
                if (rule is { RequireStatusChecks: true })
                {
                    var total = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM commit_statuses WHERE repo_id = @repoId AND sha = @sha", new { repoId, sha = pr.HeadSha });
                    var green = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM commit_statuses WHERE repo_id = @repoId AND sha = @sha AND state = 'success'", new { repoId, sha = pr.HeadSha });
                    if (total == 0 || total != green)
                        throw new ValidationException($"Status checks are not all passing ({green}/{total}).");
                }
            }

            var sig = new Signature(username, string.IsNullOrWhiteSpace(email) ? $"{username}@users.noreply.gitsrv" : email, DateTimeOffset.Now);
            var outcome = merger.Merge(repoDir, method, pr.BaseBranch, pr.HeadBranch, pr.HeadSha, pr.Title, number, sig, cfg.DeleteBranchOnMerge);

            await using var conn2 = await db.OpenAsync(ct);
            await conn2.ExecuteAsync("""
                UPDATE pull_requests SET state = 'merged', merge_sha = @mergeSha, merge_method = @method,
                    merged_by = @userId, merged_at = now(), updated_at = now()
                WHERE id = @id
                """, new { id = pr.Id, mergeSha = outcome.MergeSha, method, userId });

            // A merge into the base branch may satisfy other open PRs targeting it.
            await SyncAfterPushInternalAsync(conn2, repoId, repoDir, ct);

            // Close referenced issues ("closes #N" in the PR body or its commits) and record activity.
            var orgRepo = await conn2.QuerySingleAsync<OrgRepoSlugs>(
                "SELECT o.slug AS OrgSlug, r.slug AS RepoSlug FROM repositories r JOIN organisations o ON o.id = r.org_id WHERE r.id = @repoId",
                new { repoId });
            var closingText = pr.Body + "\n" + CommitMessages(repoDir, pr);
            await activity.RecordAsync(userId, null, repoId, "pr_merged", number, $"{username} merged PR #{number}: {pr.Title}", ct);
            await issues.LinkAndMaybeCloseAsync(repoId, orgRepo.OrgSlug, orgRepo.RepoSlug, "pr", $"#{number}", closingText, applyClosings: true, userId, username, ct);

            var orgId2 = await conn2.ExecuteScalarAsync<long>("SELECT org_id FROM repositories WHERE id = @repoId", new { repoId });
            var mergeUrl = $"{config["App:PublicBaseUrl"] ?? "http://localhost:8080"}/#/o/{orgRepo.OrgSlug}/{orgRepo.RepoSlug}/pulls/{number}";
            await enklr.LinkAndNotifyAsync(orgId2, repoId, "pull", $"#{number}", pr.Title, "merged", mergeUrl,
                $"{pr.Title} {closingText} {pr.HeadBranch}", username, "pr_merged", ct);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- push sync (called from both git transports after a receive-pack) ----

    public async Task SyncAfterPushAsync(long repoId, string repoDir, CancellationToken ct)
    {
        var gate = Lock(repoId);
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(10), ct)) return;
        try
        {
            await using var conn = await db.OpenAsync(ct);
            await SyncAfterPushInternalAsync(conn, repoId, repoDir, ct);
        }
        finally { gate.Release(); }
    }

    private async Task SyncAfterPushInternalAsync(System.Data.IDbConnection conn, long repoId, string repoDir, CancellationToken ct)
    {
        var open = (await conn.QueryAsync<PrRow>(SelectPrRow + " WHERE repo_id = @repoId AND state = 'open'", new { repoId })).ToList();
        if (open.Count == 0) return;

        using var repo = new Repository(repoDir);
        foreach (var pr in open)
        {
            var headTip = repo.Branches[pr.HeadBranch]?.Tip;
            var baseTip = repo.Branches[pr.BaseBranch]?.Tip;

            if (headTip is null)
            {
                await conn.ExecuteAsync("UPDATE pull_requests SET state = 'closed', closed_at = now(), updated_at = now() WHERE id = @id", new { pr.Id });
                continue;
            }
            if (baseTip is not null && (baseTip.Sha == headTip.Sha ||
                repo.ObjectDatabase.FindMergeBase(baseTip, headTip)?.Sha == headTip.Sha))
            {
                // base already contains head → merged outside GitSrv (or by us)
                await conn.ExecuteAsync("""
                    UPDATE pull_requests SET state = 'merged', merged_at = COALESCE(merged_at, now()), updated_at = now(),
                        merge_sha = COALESCE(merge_sha, @sha)
                    WHERE id = @id
                    """, new { pr.Id, sha = headTip.Sha });
                continue;
            }
            if (headTip.Sha != pr.HeadSha)
                await conn.ExecuteAsync("UPDATE pull_requests SET head_sha = @sha, updated_at = now() WHERE id = @id", new { pr.Id, sha = headTip.Sha });
        }
    }

    // ---- shared ----

    private const string SelectPrRow = """
        SELECT id, number, title, body, state, is_draft AS IsDraft, base_branch AS BaseBranch, head_branch AS HeadBranch,
               head_sha AS HeadSha, merge_sha AS MergeSha, merge_method AS MergeMethod, created_by AS CreatedBy,
               merged_by AS MergedBy, created_at AS CreatedAt, merged_at AS MergedAt, closed_at AS ClosedAt
        FROM pull_requests
        """;

    private async Task<PrRow> LoadRowAsync(System.Data.IDbConnection conn, long repoId, int number, System.Data.IDbTransaction? tx)
        => await conn.QuerySingleOrDefaultAsync<PrRow>(SelectPrRow + " WHERE repo_id = @repoId AND number = @number",
            new { repoId, number }, tx) ?? throw new NotFoundException("Pull request not found.");

    private static Task Touch(System.Data.IDbConnection conn, System.Data.IDbTransaction tx, long prId)
        => conn.ExecuteAsync("UPDATE pull_requests SET updated_at = now() WHERE id = @prId", new { prId }, tx);
}
