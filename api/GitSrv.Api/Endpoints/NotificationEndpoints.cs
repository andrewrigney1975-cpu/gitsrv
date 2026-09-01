using GitSrv.Api.Auth;
using GitSrv.Api.Collab;

namespace GitSrv.Api.Endpoints;

public static class NotificationEndpoints
{
    public sealed record MarkRequest(long[] Ids, bool Read);

    public static void MapNotifications(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/notifications").RequireAuth();

        g.MapGet("/", async (string? filter, CurrentUser cu, NotificationService n, CancellationToken ct) =>
            Results.Json(await n.InboxAsync(cu.RequireId(), filter == "unread", ct)));

        g.MapGet("/count", async (CurrentUser cu, NotificationService n, CancellationToken ct) =>
            Results.Json(new { unread = await n.UnreadCountAsync(cu.RequireId(), ct) }));

        g.MapPost("/mark", async (MarkRequest req, CurrentUser cu, NotificationService n, CancellationToken ct) =>
        {
            await n.MarkReadAsync(cu.RequireId(), req.Ids ?? [], req.Read, ct);
            return Results.NoContent();
        });

        g.MapPost("/read-all", async (CurrentUser cu, NotificationService n, CancellationToken ct) =>
        {
            await n.MarkAllReadAsync(cu.RequireId(), ct);
            return Results.NoContent();
        });
    }
}

public static class ActivityEndpoints
{
    public static void MapActivity(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/user/feed", async (CurrentUser cu, ActivityService act, CancellationToken ct) =>
        {
            if (!cu.IsAuthenticated) throw new UnauthorizedException();
            return Results.Json(await act.UserFeedAsync(cu.RequireId(), 50, ct));
        });

        app.MapGet("/api/orgs/{slug}/activity", async (string slug, CurrentUser cu,
            Identity.OrgService orgs, Authz.Authorizer authz, ActivityService act, CancellationToken ct) =>
        {
            var org = await orgs.GetBySlugAsync(slug, ct) ?? throw new NotFoundException("Organisation not found.");
            if (cu.IsAuthenticated)
            {
                var role = await authz.GetOrgRoleAsync(cu.RequireId(), org.Id, ct);
                var siteAdmin = await authz.IsSiteAdminAsync(cu.RequireId(), ct);
                if (role is null && !siteAdmin) throw new NotFoundException("Organisation not found.");
            }
            else throw new UnauthorizedException();
            return Results.Json(await act.OrgFeedAsync(org.Id, 50, ct));
        });
    }
}
