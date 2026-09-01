using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Git;

namespace GitSrv.Api.Endpoints;

public static class PullRequestEndpoints
{
    public sealed record CreatePrRequest(string Title, string Body, string BaseBranch, string HeadBranch, bool IsDraft);
    public sealed record UpdatePrRequest(string? Title, string? Body, bool? IsDraft);
    public sealed record CommentRequest(string Body, long? ThreadId, string? FilePath, int? Line, string? Side, bool Pending);
    public sealed record ReviewRequest(string State, string Body);
    public sealed record ReviewersRequest(string[] Usernames);
    public sealed record MergeRequest(string Method);
    public sealed record StateRequest(string State);
    public sealed record CompareQuery(string Base, string Head);

    public static void MapPullRequests(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}/pulls");

        // compare preview for the "new PR" screen
        g.MapGet("/compare", async (string slug, string repoSlug, string @base, string head,
            CurrentUser cu, RepoBrowseService browse, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = browse.Open(b);
            return Results.Json(r.Compare(@base, head));
        });

        g.MapGet("/", async (string slug, string repoSlug, string? state,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await prs.ListAsync(b.RepoId, state ?? "open", ct));
        });

        g.MapGet("/{number:int}", async (string slug, string repoSlug, int number,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            var detail = await prs.GetAsync(b.RepoId, b.RepoDir, number, cu.UserId, ct);
            return Results.Json(new { detail, myPermission = RepoPermissions.ToDbValue(b.Permission) });
        });

        g.MapPost("/", async (string slug, string repoSlug, CreatePrRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Write(browse, slug, repoSlug, cu, ct);
            var number = await prs.CreateAsync(b.RepoId, b.RepoDir, cu.RequireId(),
                req.Title ?? "", req.Body ?? "", req.BaseBranch ?? "", req.HeadBranch ?? "", req.IsDraft, ct);
            return Results.Json(new { number }, statusCode: 201);
        });

        g.MapPatch("/{number:int}", async (string slug, string repoSlug, int number, UpdatePrRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.UpdateAsync(b.RepoId, number, cu.RequireId(), req.Title, req.Body, req.IsDraft, ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/state", async (string slug, string repoSlug, int number, StateRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            if (req.State is not ("open" or "closed")) throw new ValidationException("State must be open or closed.");
            await prs.SetStateAsync(b.RepoId, number, req.State, ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/comments", async (string slug, string repoSlug, int number, CommentRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            var id = await prs.CommentAsync(b.RepoId, number, cu.RequireId(), req.Body ?? "",
                req.ThreadId, req.FilePath, req.Line, req.Side ?? "new", req.Pending, ct);
            return Results.Json(new { id }, statusCode: 201);
        });

        g.MapPatch("/{number:int}/comments/{commentId:long}", async (string slug, string repoSlug, int number, long commentId,
            CommentRequest req, CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.EditCommentAsync(b.RepoId, number, cu.RequireId(), commentId, req.Body ?? "", ct);
            return Results.NoContent();
        });

        g.MapDelete("/{number:int}/comments/{commentId:long}", async (string slug, string repoSlug, int number, long commentId,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.DeleteCommentAsync(b.RepoId, number, cu.RequireId(), commentId, b.Permission >= RepoPermission.Admin, ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/threads/{threadId:long}/resolve", async (string slug, string repoSlug, int number, long threadId,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.ResolveThreadAsync(b.RepoId, number, cu.RequireId(), threadId, true, ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/threads/{threadId:long}/unresolve", async (string slug, string repoSlug, int number, long threadId,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.ResolveThreadAsync(b.RepoId, number, cu.RequireId(), threadId, false, ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/reviews", async (string slug, string repoSlug, int number, ReviewRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.SubmitReviewAsync(b.RepoId, number, cu.RequireId(), req.State ?? "comment", req.Body ?? "", ct);
            return Results.NoContent();
        });

        g.MapPut("/{number:int}/reviewers", async (string slug, string repoSlug, int number, ReviewersRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, CancellationToken ct) =>
        {
            var b = await Triage(browse, slug, repoSlug, cu, ct);
            await prs.SetReviewersAsync(b.RepoId, number, req.Usernames ?? [], ct);
            return Results.NoContent();
        });

        g.MapPost("/{number:int}/merge", async (string slug, string repoSlug, int number, MergeRequest req,
            CurrentUser cu, RepoBrowseService browse, PullRequestService prs, AccountService accounts, CancellationToken ct) =>
        {
            var b = await Write(browse, slug, repoSlug, cu, ct);
            var user = await accounts.GetAsync(cu.RequireId(), ct)!;
            await prs.MergeAsync(b.RepoId, b.RepoDir, number, cu.RequireId(), user!.Username, user.Email, req.Method ?? "merge", ct);
            return Results.NoContent();
        });
    }

    private static async Task<Git.BrowseContext> Write(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, CancellationToken ct)
        => await Require(browse, slug, repoSlug, cu, RepoPermission.Write, ct);

    private static async Task<Git.BrowseContext> Triage(RepoBrowseService browse, string slug, string repoSlug, CurrentUser cu, CancellationToken ct)
        => await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);

    private static async Task<Git.BrowseContext> Require(RepoBrowseService browse, string slug, string repoSlug,
        CurrentUser cu, RepoPermission min, CancellationToken ct)
    {
        if (!cu.IsAuthenticated) throw new UnauthorizedException();
        var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
        if (b.Permission < min) throw new ForbiddenException();
        return b;
    }
}
