using System.Diagnostics;
using Dapper;
using GitSrv.Api.Data;
using Npgsql;

namespace GitSrv.Api.Git;

/// <summary>
/// Performs one-time imports of external repositories (a public clone URL, e.g. GitHub) into a
/// GitSrv bare repo. Repos with <c>import_status = 'pending'</c> are cloned one at a time; the
/// source URL has already passed <see cref="Ops.UrlGuard"/> at request time.
/// </summary>
public sealed class RepoImportWorker(NpgsqlDataSource dataSource, GitStorage storage, ILogger<RepoImportWorker> logger)
    : BackgroundService
{
    private sealed record Pending(long Id, long OrgId, string ImportSource, string DefaultBranch);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Repo import sweep failed"); }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);

        // Claim one pending import.
        var job = await conn.QuerySingleOrDefaultAsync<Pending>("""
            UPDATE repositories SET import_status = 'importing'
            WHERE id = (SELECT id FROM repositories WHERE import_status = 'pending' ORDER BY created_at LIMIT 1 FOR UPDATE SKIP LOCKED)
            RETURNING id, org_id AS OrgId, import_source AS ImportSource, default_branch AS DefaultBranch
            """);
        if (job is null) return;

        var dir = storage.RepoPath(job.OrgId, job.Id);
        logger.LogInformation("Importing repo {RepoId} from {Source}", job.Id, job.ImportSource);
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.CreateDirectory(Path.GetDirectoryName(dir)!);

            await Git(null, ["clone", "--bare", "--quiet", job.ImportSource, dir], TimeSpan.FromMinutes(30), ct);

            // Detach from the source, apply GitSrv's repo config + hooks, note the real default branch.
            await Git(dir, ["remote", "remove", "origin"], TimeSpan.FromSeconds(20), ct, ignoreErrors: true);
            var head = (await Git(dir, ["symbolic-ref", "--short", "HEAD"], TimeSpan.FromSeconds(20), ct, ignoreErrors: true)).Trim();
            foreach (var (k, v) in new[]
            {
                ("uploadpack.allowFilter", "true"), ("repack.writeBitmaps", "true"),
                ("core.commitGraph", "true"), ("gc.writeCommitGraph", "true"),
            })
                await Git(dir, ["config", k, v], TimeSpan.FromSeconds(20), ct, ignoreErrors: true);
            storage.WriteHooks(dir);
            await Git(dir, ["commit-graph", "write", "--reachable"], TimeSpan.FromMinutes(5), ct, ignoreErrors: true);

            var size = storage.MeasureSize(job.OrgId, job.Id);
            await conn.ExecuteAsync("""
                UPDATE repositories SET import_status = 'completed', import_error = '',
                    default_branch = COALESCE(NULLIF(@head, ''), default_branch),
                    size_bytes = @size, pushed_at = now(), updated_at = now()
                WHERE id = @id
                """, new { id = job.Id, head, size });
            logger.LogInformation("Imported repo {RepoId} ({Bytes} bytes)", job.Id, size);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Import failed for repo {RepoId}", job.Id);
            if (Directory.Exists(dir)) { try { Directory.Delete(dir, true); } catch { } }
            await conn.ExecuteAsync(
                "UPDATE repositories SET import_status = 'failed', import_error = @err, updated_at = now() WHERE id = @id",
                new { id = job.Id, err = Trim(ex.Message, 500) });
        }
    }

    private static async Task<string> Git(string? repoDir, string[] args, TimeSpan timeout, CancellationToken ct, bool ignoreErrors = false)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
        };
        // Never let a hung remote block a worker thread; disable interactive prompts.
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (repoDir is not null) { psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(repoDir); }
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = await proc.StandardError.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);
        if (proc.ExitCode != 0 && !ignoreErrors)
            throw new InvalidOperationException($"git {args[0]} failed: {(stderr.Length > 0 ? stderr : stdout).Trim()}");
        return stdout;
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n];
}
