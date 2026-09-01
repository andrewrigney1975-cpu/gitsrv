using System.Diagnostics;
using Dapper;
using GitSrv.Api.Data;
using Npgsql;

namespace GitSrv.Api.Git;

/// <summary>
/// Periodically runs git object maintenance (incremental repack, commit-graph, multi-pack-index,
/// auto gc) on repositories pushed since they were last maintained. Keeps clone/fetch fast and
/// bounds the pack count without blocking pushes.
/// </summary>
public sealed class GitMaintenanceWorker(NpgsqlDataSource dataSource, GitStorage storage, ILogger<GitMaintenanceWorker> logger)
    : BackgroundService
{
    private sealed record RepoRow(long Id, long OrgId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await SweepAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Git maintenance sweep failed"); }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        var due = (await conn.QueryAsync<RepoRow>("""
            SELECT id, org_id AS OrgId FROM repositories
            WHERE pushed_at IS NOT NULL
              AND (last_maintained_at IS NULL OR pushed_at > last_maintained_at)
            ORDER BY pushed_at
            LIMIT 20
            """)).ToList();

        foreach (var r in due)
        {
            if (ct.IsCancellationRequested) break;
            var dir = storage.RepoPath(r.OrgId, r.Id);
            if (!Directory.Exists(dir)) continue;
            var sw = Stopwatch.StartNew();
            try
            {
                await Git(dir, ["-c", "gc.auto=6700", "gc", "--auto", "--quiet"], ct);
                await Git(dir, ["commit-graph", "write", "--reachable", "--changed-paths"], ct);
                await Git(dir, ["multi-pack-index", "write"], ct);
                await conn.ExecuteAsync("UPDATE repositories SET last_maintained_at = now() WHERE id = @id", new { id = r.Id });
                logger.LogInformation("Maintained repo {RepoId} in {Ms}ms", r.Id, sw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Maintenance failed for repo {RepoId}", r.Id);
            }
        }
    }

    private static async Task Git(string dir, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(dir);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
    }
}
