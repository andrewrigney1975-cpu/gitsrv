using System.Security.Cryptography;
using System.Text;
using GitSrv.Api.Auth;
using GitSrv.Api.Authz;
using GitSrv.Api.Git;
using Microsoft.Extensions.Caching.Memory;

namespace GitSrv.Api.Endpoints;

/// <summary>
/// Read-only repository browsing: tree, blob, raw, history, commit detail, blame, commit graph, and
/// a combined repo-home payload. Not behind <c>RequireAuth</c> — anonymous callers can browse
/// public repos; everything else 404s.
/// </summary>
public static class RepoBrowseEndpoints
{
    public static void MapRepoBrowse(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/orgs/{slug}/repos/{repoSlug}/browse");

        // Repo home: refs + latest commit + README (rendered) + language bar, in one round trip.
        g.MapGet("/overview", async (string slug, string repoSlug, string? @ref, CurrentUser cu,
            RepoBrowseService svc, IMemoryCache cache, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);

            var refs = r.Refs();
            object? home = null;
            if (!refs.IsEmpty)
            {
                var head = @ref ?? refs.DefaultBranch ?? "HEAD";
                var tree = r.Tree(head, "");
                string? readmeHtml = null, readmeName = null;
                foreach (var name in MarkdownRenderer.ReadmeCandidates)
                {
                    var hit = tree.Entries.FirstOrDefault(e => e.Type == "blob" && e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (hit is null) continue;
                    var blob = r.Blob(head, hit.Path);
                    if (blob.Text is not null)
                    {
                        readmeName = hit.Name;
                        var isMarkdown = hit.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || !hit.Name.Contains('.');
                        // Rendering is the main CPU cost of this endpoint — cache by content hash.
                        var cacheKey = $"md:{blob.Sha}";
                        readmeHtml = cache.GetOrCreate(cacheKey, e =>
                        {
                            e.SlidingExpiration = TimeSpan.FromMinutes(30);
                            e.Size = blob.Text.Length;
                            return isMarkdown
                                ? MarkdownRenderer.ToHtml(blob.Text)
                                : $"<pre>{System.Net.WebUtility.HtmlEncode(blob.Text)}</pre>";
                        });
                    }
                    break;
                }
                var languages = Languages.Summarise(FlattenBlobs(r, head));
                home = new { @ref = head, tree.Commit, entries = tree.Entries, readmeName, readmeHtml, languages };
            }

            return Results.Json(new
            {
                repo = new { b.RepoId, b.OrgSlug, b.RepoSlug, b.Name, b.Description, b.Visibility, b.DefaultBranch, b.IsArchived, b.SizeBytes, b.PushedAt },
                myPermission = RepoPermissions.ToDbValue(b.Permission),
                refs,
                home,
            });
        });

        g.MapGet("/refs", async (string slug, string repoSlug, CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            return Results.Json(r.Refs());
        });

        g.MapGet("/tree/{ref}/{**path}", async (string slug, string repoSlug, string @ref, string? path,
            CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            return Results.Json(r.Tree(@ref, path ?? ""));
        });

        g.MapGet("/blob/{ref}/{**path}", async (string slug, string repoSlug, string @ref, string path,
            HttpContext ctx, CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            var blob = r.Blob(@ref, path);
            if (ConditionalHit(ctx, blob.Sha)) return Results.StatusCode(304);
            return Results.Json(new { blob, language = Languages.Detect(System.IO.Path.GetFileName(path)) });
        });

        g.MapGet("/raw/{ref}/{**path}", async (string slug, string repoSlug, string @ref, string path,
            HttpContext ctx, CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            var (content, _, fileName, sha) = r.RawBlob(@ref, path);
            if (ConditionalHit(ctx, sha)) { content.Dispose(); return Results.StatusCode(304); }
            // Force download as octet-stream so the browser never renders untrusted content inline.
            return Results.Stream(content, "application/octet-stream", fileDownloadName: fileName);
        });

        g.MapGet("/commits/{ref}", async (string slug, string repoSlug, string @ref, string? path,
            int? page, CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            const int pageSize = 30;
            var p = Math.Max(1, page ?? 1);
            var commits = r.Log(@ref, path, (p - 1) * pageSize, pageSize + 1);
            return Results.Json(new { page = p, pageSize, hasMore = commits.Count > pageSize, commits = commits.Take(pageSize) });
        });

        g.MapGet("/commit/{sha}", async (string slug, string repoSlug, string sha,
            CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            return Results.Json(r.Commit(sha));
        });

        g.MapGet("/blame/{ref}/{**path}", async (string slug, string repoSlug, string @ref, string path,
            CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            return Results.Json(r.Blame(@ref, path));
        });

        g.MapGet("/graph", async (string slug, string repoSlug, int? limit,
            CurrentUser cu, RepoBrowseService svc, CancellationToken ct) =>
        {
            var b = await svc.ResolveAsync(slug, repoSlug, cu.UserId, ct);
            using var r = svc.Open(b);
            return Results.Json(r.Graph(Math.Clamp(limit ?? 120, 1, 400)));
        });
    }

    /// <summary>Sets a strong ETag + immutable Cache-Control and returns true if the client already has it.</summary>
    private static bool ConditionalHit(HttpContext ctx, string contentKey)
    {
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contentKey)))[..16] + "\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "private, max-age=60";
        return ctx.Request.Headers.IfNoneMatch.ToString() == etag;
    }

    private static IEnumerable<(string, long)> FlattenBlobs(RepoReader r, string @ref)
    {
        var stack = new Stack<string>();
        stack.Push("");
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            TreeView tv;
            try { tv = r.Tree(@ref, dir); } catch { continue; }
            foreach (var e in tv.Entries)
            {
                if (e.Type == "tree") stack.Push(e.Path);
                else if (e.Type == "blob") yield return (e.Name, e.Size);
            }
        }
    }
}
