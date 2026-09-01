using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Collab;
using GitSrv.Api.Git;

namespace GitSrv.Api.Endpoints;

public static class IssueEndpoints
{
    public sealed record CreateIssueRequest(string Title, string Body, long[]? LabelIds, string[]? Assignees, long? MilestoneId);
    public sealed record UpdateIssueRequest(string? Title, string? Body, long? MilestoneId, bool ClearMilestone);
    public sealed record StateRequest(string State);
    public sealed record CommentRequest(string Body);
    public sealed record LabelsRequest(long[] LabelIds);
    public sealed record AssigneesRequest(string[] Usernames);
    public sealed record LabelRequest(string Name, string Color, string Description);
    public sealed record MilestoneRequest(string Title, string Description, DateOnly? DueOn, string? State);

    public static void MapIssues(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}");

        // ---- issues ----
        g.MapGet("/issues", async (string slug, string repoSlug, string? state, string? label, string? assignee, long? milestoneId,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await issues.ListAsync(b.RepoId, state ?? "open", label, assignee, milestoneId, ct));
        });

        g.MapGet("/issues/{number:int}", async (string slug, string repoSlug, int number,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(new { detail = await issues.GetAsync(b.RepoId, b.OrgSlug, b.RepoSlug, number, ct),
                myPermission = RepoPermissions.ToDbValue(b.Permission) });
        });

        g.MapPost("/issues", async (string slug, string repoSlug, CreateIssueRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, AccountService accounts, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            var user = (await accounts.GetAsync(cu.RequireId(), ct))!;
            var number = await issues.CreateAsync(b.RepoId, b.OrgSlug, b.RepoSlug, user.Id, user.Username,
                req.Title ?? "", req.Body ?? "", req.LabelIds ?? [], req.Assignees ?? [], req.MilestoneId, ct);
            return Results.Json(new { number }, statusCode: 201);
        });

        g.MapPatch("/issues/{number:int}", async (string slug, string repoSlug, int number, UpdateIssueRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            await issues.UpdateAsync(b.RepoId, number, cu.RequireId(), req.Title, req.Body, req.MilestoneId, req.ClearMilestone, ct);
            return Results.NoContent();
        });

        g.MapPost("/issues/{number:int}/state", async (string slug, string repoSlug, int number, StateRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, AccountService accounts, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            var user = (await accounts.GetAsync(cu.RequireId(), ct))!;
            await issues.SetStateAsync(b.RepoId, b.OrgSlug, b.RepoSlug, number, user.Id, user.Username, req.State ?? "open", ct);
            return Results.NoContent();
        });

        g.MapPost("/issues/{number:int}/comments", async (string slug, string repoSlug, int number, CommentRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, AccountService accounts, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            var user = (await accounts.GetAsync(cu.RequireId(), ct))!;
            var id = await issues.CommentAsync(b.RepoId, b.OrgSlug, b.RepoSlug, number, user.Id, user.Username, req.Body ?? "", ct);
            return Results.Json(new { id }, statusCode: 201);
        });

        g.MapPatch("/issues/{number:int}/comments/{commentId:long}", async (string slug, string repoSlug, int number, long commentId, CommentRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            await issues.EditCommentAsync(cu.RequireId(), commentId, req.Body ?? "", ct);
            return Results.NoContent();
        });

        g.MapDelete("/issues/{number:int}/comments/{commentId:long}", async (string slug, string repoSlug, int number, long commentId,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            await issues.DeleteCommentAsync(cu.RequireId(), commentId, b.Permission >= RepoPermission.Admin, ct);
            return Results.NoContent();
        });

        g.MapPut("/issues/{number:int}/labels", async (string slug, string repoSlug, int number, LabelsRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            await issues.SetLabelsAsync(b.RepoId, number, cu.RequireId(), req.LabelIds ?? [], ct);
            return Results.NoContent();
        });

        g.MapPut("/issues/{number:int}/assignees", async (string slug, string repoSlug, int number, AssigneesRequest req,
            CurrentUser cu, RepoBrowseService browse, IssueService issues, AccountService accounts, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Triage, ct);
            var user = (await accounts.GetAsync(cu.RequireId(), ct))!;
            await issues.SetAssigneesAsync(b.RepoId, b.OrgSlug, b.RepoSlug, number, user.Id, user.Username, req.Usernames ?? [], ct);
            return Results.NoContent();
        });

        // ---- labels ----
        g.MapGet("/labels", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await issues.ListLabelsAsync(b.RepoId, ct));
        });
        g.MapPost("/labels", async (string slug, string repoSlug, LabelRequest req, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            var id = await issues.CreateLabelAsync(b.RepoId, req.Name ?? "", req.Color ?? "", req.Description ?? "", ct);
            return Results.Json(new { id }, statusCode: 201);
        });
        g.MapPatch("/labels/{id:long}", async (string slug, string repoSlug, long id, LabelRequest req, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            await issues.UpdateLabelAsync(b.RepoId, id, req.Name ?? "", req.Color ?? "", req.Description ?? "", ct);
            return Results.NoContent();
        });
        g.MapDelete("/labels/{id:long}", async (string slug, string repoSlug, long id, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            await issues.DeleteLabelAsync(b.RepoId, id, ct);
            return Results.NoContent();
        });

        // ---- milestones ----
        g.MapGet("/milestones", async (string slug, string repoSlug, string? state, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await issues.ListMilestonesAsync(b.RepoId, state ?? "open", ct));
        });
        g.MapPost("/milestones", async (string slug, string repoSlug, MilestoneRequest req, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            var id = await issues.CreateMilestoneAsync(b.RepoId, req.Title ?? "", req.Description ?? "", req.DueOn, ct);
            return Results.Json(new { id }, statusCode: 201);
        });
        g.MapPatch("/milestones/{id:long}", async (string slug, string repoSlug, long id, MilestoneRequest req, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            await issues.UpdateMilestoneAsync(b.RepoId, id, req.Title ?? "", req.Description ?? "", req.DueOn, req.State ?? "open", ct);
            return Results.NoContent();
        });
        g.MapDelete("/milestones/{id:long}", async (string slug, string repoSlug, long id, CurrentUser cu, RepoBrowseService browse, IssueService issues, CancellationToken ct) =>
        {
            var b = await Require(browse, slug, repoSlug, cu, RepoPermission.Admin, ct);
            await issues.DeleteMilestoneAsync(b.RepoId, id, ct);
            return Results.NoContent();
        });

        // ---- watch ----
        g.MapGet("/watch", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, NotificationService notif, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(new { watching = cu.IsAuthenticated && await notif.IsWatchingAsync(b.RepoId, cu.RequireId(), ct) });
        });
        g.MapPut("/watch", async (string slug, string repoSlug, WatchRequest req, CurrentUser cu, RepoBrowseService browse, NotificationService notif, CancellationToken ct) =>
        {
            if (!cu.IsAuthenticated) throw new UnauthorizedException();
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            await notif.SetWatchAsync(b.RepoId, cu.RequireId(), req.Watching, ct);
            return Results.NoContent();
        });

        // ---- repo activity ----
        g.MapGet("/activity", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService browse, ActivityService act, CancellationToken ct) =>
        {
            var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            return Results.Json(await act.RepoFeedAsync(b.RepoId, 50, ct));
        });
    }

    public sealed record WatchRequest(bool Watching);

    private static async Task<BrowseContext> Require(RepoBrowseService browse, string slug, string repoSlug,
        CurrentUser cu, RepoPermission min, CancellationToken ct)
    {
        if (!cu.IsAuthenticated) throw new UnauthorizedException();
        var b = await browse.ResolveAsync(slug, repoSlug, cu.UserId, ct);
        if (b.Permission < min) throw new ForbiddenException();
        return b;
    }
}
