using System.Text.Json;
using Dapper;
using GitSrv.Api.Data;
using GitSrv.Api.Git;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Actions;

// ---- read DTOs ----
public sealed record RunListItem(long Id, int Number, string WorkflowName, string Event, string Ref, string HeadSha,
    int? PrNumber, string Status, string? Conclusion, DateTime CreatedAt);
public sealed record JobView(long Id, string Name, string RunsOn, string Status, string? Conclusion,
    DateTime? StartedAt, DateTime? CompletedAt, IReadOnlyList<StepView> Steps);
public sealed record StepView(int Number, string Name, string Status, string? Conclusion, int? ExitCode);
public sealed record RunDetail(RunListItem Run, IReadOnlyList<JobView> Jobs);
public sealed record LogLine(long Seq, int? StepNumber, string Line);

// ---- runner contract ----
public sealed record ClaimedStep(int Number, string Name, string Kind, string Run, string Uses,
    Dictionary<string, string> With, Dictionary<string, string> Env, string? If, string? Shell,
    string? WorkingDirectory, bool ContinueOnError);
public sealed record ClaimedJob(long JobId, long RunId, int RunNumber, string JobToken, string CloneUrl, string HeadSha,
    string RunsOn, string? Container, Dictionary<string, string> Matrix, Dictionary<string, string> Env,
    Dictionary<string, string> Secrets, Dictionary<string, string> Github, IReadOnlyList<ClaimedStep> Steps);

public sealed class ActionsService(Db db, GitStorage storage, ChecksService checks, SecretsService secrets,
    Collab.ActivityService activity, Integrations.EnklrService enklr, IConfiguration config, ILogger<ActionsService> logger)
{
    private readonly string _internalToken = config["GitSrv:InternalToken"] ?? "";
    private const string ApiInternalBase = "http://api:8080"; // reachable from the runner on the compose network

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- dispatch (called from post-receive and PR events) ----

    public async Task DispatchAsync(long orgId, string orgSlug, string repoSlug, long repoId, string publicBaseUrl,
        string @event, string @ref, string headSha, int? prNumber, long? triggeredBy, CancellationToken ct)
    {
        List<Workflow> workflows;
        try
        {
            using var reader = new RepoReader(storage.RepoPath(orgId, repoId));
            TreeView dir;
            try { dir = reader.Tree(headSha, ".gitsrv/workflows"); }
            catch { return; } // no workflows dir
            workflows = [];
            foreach (var e in dir.Entries.Where(e => e.Type == "blob" && (e.Name.EndsWith(".yml") || e.Name.EndsWith(".yaml"))))
            {
                var blob = reader.Blob(headSha, e.Path);
                if (blob.Text is null) continue;
                try { workflows.Add(WorkflowParser.Parse(blob.Text, e.Path)); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to parse workflow {Path}", e.Path); }
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "Workflow dispatch read failed for repo {RepoId}", repoId); return; }

        var branch = @ref.StartsWith("refs/heads/") ? @ref["refs/heads/".Length..] : @ref;

        foreach (var wf in workflows)
        {
            bool matches = @event switch
            {
                "push" => wf.On.Push && (wf.On.PushBranches is null || wf.On.PushBranches.Any(b => GlobMatch(b, branch))),
                "pull_request" => wf.On.PullRequest && (wf.On.PrBranches is null || wf.On.PrBranches.Any(b => GlobMatch(b, branch))),
                _ => false,
            };
            if (!matches) continue;

            await CreateRunAsync(orgId, orgSlug, repoSlug, repoId, publicBaseUrl, wf, @event, @ref, headSha, prNumber, triggeredBy, ct);
        }
    }

    private async Task CreateRunAsync(long orgId, string orgSlug, string repoSlug, long repoId, string publicBaseUrl,
        Workflow wf, string @event, string @ref, string headSha, int? prNumber, long? triggeredBy, CancellationToken ct)
    {
        await db.InTransactionAsync<object?>(async (conn, tx) =>
        {
            var number = await conn.ExecuteScalarAsync<int>(
                "SELECT COALESCE(MAX(number), 0) + 1 FROM workflow_runs WHERE repo_id = @repoId", new { repoId }, tx);
            var runId = await conn.ExecuteScalarAsync<long>("""
                INSERT INTO workflow_runs (repo_id, number, workflow_name, workflow_path, event, ref, head_sha, pr_number, triggered_by)
                VALUES (@repoId, @number, @name, @path, @event, @ref, @headSha, @prNumber, @triggeredBy) RETURNING id
                """, new { repoId, number, name = wf.Name, path = wf.Path, @event, @ref, headSha, prNumber, triggeredBy }, tx);

            foreach (var job in wf.Jobs)
            {
                foreach (var combo in ExpandMatrix(job.Matrix))
                {
                    var jobName = combo.Count == 0 ? job.Name : $"{job.Name} ({string.Join(", ", combo.Values)})";
                    var jobId = await conn.ExecuteScalarAsync<long>("""
                        INSERT INTO workflow_jobs (run_id, name, runs_on, matrix_json, needs_json)
                        VALUES (@runId, @jobName, @runsOn, @matrix, @needs) RETURNING id
                        """, new { runId, jobName, runsOn = job.Container ?? job.RunsOn, matrix = JsonSerializer.Serialize(combo, Json), needs = JsonSerializer.Serialize(job.Needs, Json) }, tx);

                    int n = 0;
                    foreach (var step in job.Steps)
                    {
                        n++;
                        var kind = step.Uses is not null
                            ? (step.Uses.StartsWith("actions/checkout") ? "checkout" : "uses")
                            : "run";
                        var spec = JsonSerializer.Serialize(new
                        {
                            name = step.Name ?? (step.Run is not null ? FirstLine(step.Run) : step.Uses),
                            run = step.Run ?? "",
                            uses = step.Uses ?? "",
                            with = step.With,
                            env = MergeEnv(wf, job, step),
                            @if = step.If,
                            shell = step.Shell,
                            workingDirectory = step.WorkingDirectory,
                            continueOnError = step.ContinueOnError,
                        }, Json);
                        await conn.ExecuteAsync("""
                            INSERT INTO job_steps (job_id, number, name, kind, spec_json)
                            VALUES (@jobId, @n, @name, @kind, @spec)
                            """, new { jobId, n, name = step.Name ?? kind, kind, spec }, tx);
                    }

                    // pending commit status per job
                    await conn.ExecuteAsync("""
                        INSERT INTO commit_statuses (repo_id, sha, context, state, description, target_url)
                        VALUES (@repoId, @headSha, @context, 'pending', 'Queued', @url)
                        ON CONFLICT (repo_id, sha, context) DO UPDATE SET state = 'pending', description = 'Queued', updated_at = now()
                        """, new { repoId, headSha, context = $"{wf.Name} / {jobName}", url = $"{publicBaseUrl}/#/o/{orgSlug}/{repoSlug}/actions/{number}" }, tx);
                }
            }
            return null;
        }, ct);

        await activity.RecordAsync(triggeredBy, orgId, repoId, "workflow_run", null, $"{wf.Name} run queued for {headSha[..7]}", ct);
    }

    // ---- reads ----

    public async Task<IReadOnlyList<RunListItem>> ListRunsAsync(long repoId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        return (await conn.QueryAsync<RunListItem>("""
            SELECT id, number, workflow_name AS WorkflowName, event, ref, head_sha AS HeadSha, pr_number AS PrNumber,
                   status, conclusion, created_at AS CreatedAt
            FROM workflow_runs WHERE repo_id = @repoId ORDER BY number DESC LIMIT 50
            """, new { repoId })).ToList();
    }

    public async Task<RunDetail> GetRunAsync(long repoId, int number, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var run = await conn.QuerySingleOrDefaultAsync<RunListItem>("""
            SELECT id, number, workflow_name AS WorkflowName, event, ref, head_sha AS HeadSha, pr_number AS PrNumber,
                   status, conclusion, created_at AS CreatedAt
            FROM workflow_runs WHERE repo_id = @repoId AND number = @number
            """, new { repoId, number }) ?? throw new NotFoundException("Run not found.");

        var jobs = (await conn.QueryAsync<JobRow>("""
            SELECT id, name, runs_on AS RunsOn, status, conclusion, started_at AS StartedAt, completed_at AS CompletedAt
            FROM workflow_jobs WHERE run_id = @runId ORDER BY id
            """, new { runId = run.Id })).ToList();

        var result = new List<JobView>();
        foreach (var j in jobs)
        {
            var steps = (await conn.QueryAsync<StepView>("""
                SELECT number, name, status, conclusion, exit_code AS ExitCode FROM job_steps WHERE job_id = @jobId ORDER BY number
                """, new { jobId = j.Id })).ToList();
            result.Add(new JobView(j.Id, j.Name, j.RunsOn, j.Status, j.Conclusion, j.StartedAt, j.CompletedAt, steps));
        }
        return new RunDetail(run, result);
    }

    public async Task<IReadOnlyList<LogLine>> JobLogsAsync(long repoId, long jobId, long afterSeq, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var owns = await conn.ExecuteScalarAsync<bool>("""
            SELECT EXISTS (SELECT 1 FROM workflow_jobs j JOIN workflow_runs r ON r.id = j.run_id WHERE j.id = @jobId AND r.repo_id = @repoId)
            """, new { jobId, repoId });
        if (!owns) throw new NotFoundException("Job not found.");
        return (await conn.QueryAsync<LogLine>("""
            SELECT seq, step_number AS StepNumber, line FROM job_logs WHERE job_id = @jobId AND seq > @afterSeq ORDER BY seq LIMIT 2000
            """, new { jobId, afterSeq })).ToList();
    }

    public async Task RerunAsync(long orgId, string orgSlug, string repoSlug, long repoId, string publicBaseUrl, int number, long userId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var run = await conn.QuerySingleOrDefaultAsync<RunRow>(
            "SELECT id, workflow_path AS WorkflowPath, event, ref, head_sha AS HeadSha, pr_number AS PrNumber FROM workflow_runs WHERE repo_id = @repoId AND number = @number",
            new { repoId, number }) ?? throw new NotFoundException("Run not found.");

        using var reader = new RepoReader(storage.RepoPath(orgId, repoId));
        var blob = reader.Blob(run.HeadSha, run.WorkflowPath);
        if (blob.Text is null) throw new NotFoundException("Workflow file not found at that commit.");
        var wf = WorkflowParser.Parse(blob.Text, run.WorkflowPath);
        await CreateRunAsync(orgId, orgSlug, repoSlug, repoId, publicBaseUrl, wf, run.Event, run.Ref, run.HeadSha, run.PrNumber, userId, ct);
    }

    // ---- runner contract ----

    public async Task<ClaimedJob?> ClaimAsync(string runnerId, string publicBaseUrl, CancellationToken ct)
    {
        return await db.InTransactionAsync(async (conn, tx) =>
        {
            var job = await conn.QuerySingleOrDefaultAsync<ClaimRow>("""
                SELECT j.id AS JobId, j.run_id AS RunId, j.matrix_json AS MatrixJson, j.runs_on AS RunsOn,
                       r.number AS RunNumber, r.head_sha AS HeadSha, r.repo_id AS RepoId, rp.slug AS RepoSlug, o.slug AS OrgSlug, o.id AS OrgId
                FROM workflow_jobs j
                JOIN workflow_runs r ON r.id = j.run_id
                JOIN repositories rp ON rp.id = r.repo_id
                JOIN organisations o ON o.id = rp.org_id
                WHERE j.status = 'queued'
                ORDER BY j.id
                FOR UPDATE OF j SKIP LOCKED
                LIMIT 1
                """, transaction: tx);
            if (job is null) return (ClaimedJob?)null;

            await conn.ExecuteAsync("UPDATE workflow_jobs SET status = 'running', runner_id = @runnerId, started_at = now() WHERE id = @id",
                new { runnerId, id = job.JobId }, tx);
            await conn.ExecuteAsync("UPDATE workflow_runs SET status = 'running', started_at = COALESCE(started_at, now()) WHERE id = @id",
                new { id = job.RunId }, tx);
            await conn.ExecuteAsync("""
                UPDATE commit_statuses SET state = 'pending', description = 'In progress', updated_at = now()
                WHERE repo_id = @repoId AND sha = @sha AND context LIKE @ctx
                """, new { repoId = job.RepoId, sha = job.HeadSha, ctx = "% / " + (await JobName(conn, job.JobId, tx)) }, tx);

            var token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
            await conn.ExecuteAsync("INSERT INTO job_tokens (token_hash, job_id, expires_at) VALUES (@hash, @jobId, now() + interval '2 hours')",
                new { hash = Hash(token), jobId = job.JobId }, tx);

            var steps = (await conn.QueryAsync<string>("SELECT spec_json FROM job_steps WHERE job_id = @jobId ORDER BY number", new { jobId = job.JobId }, tx)).ToList();
            var claimedSteps = steps.Select((s, i) =>
            {
                var d = JsonSerializer.Deserialize<StepSpec>(s, Json)!;
                var kind = string.IsNullOrEmpty(d.Uses) ? "run" : (d.Uses.StartsWith("actions/checkout") ? "checkout" : "uses");
                return new ClaimedStep(i + 1, d.Name ?? "step", kind, d.Run ?? "", d.Uses ?? "",
                    d.With ?? [], d.Env ?? [], d.If, d.Shell, d.WorkingDirectory, d.ContinueOnError);
            }).ToList();

            var matrix = JsonSerializer.Deserialize<Dictionary<string, string>>(job.MatrixJson, Json) ?? [];
            var secretMap = await secrets.ResolveForJobAsync(job.OrgId, job.RepoId, ct);

            var github = new Dictionary<string, string>
            {
                ["repository"] = $"{job.OrgSlug}/{job.RepoSlug}",
                ["sha"] = job.HeadSha,
                ["ref"] = job.HeadSha,
                ["run_number"] = job.RunNumber.ToString(),
                ["server_url"] = publicBaseUrl,
            };

            var cloneUrl = $"http://x-internal:{Uri.EscapeDataString(_internalToken)}@{ApiInternalBase["http://".Length..]}/{job.OrgSlug}/{job.RepoSlug}.git";
            return (ClaimedJob?)new ClaimedJob(job.JobId, job.RunId, job.RunNumber, token,
                cloneUrl, job.HeadSha, job.RunsOn, null,
                matrix, [], new Dictionary<string, string>(secretMap), github, claimedSteps);
        }, ct);
    }

    public async Task<long> ValidateJobTokenAsync(string token, long jobId, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        var ok = await conn.ExecuteScalarAsync<long?>(
            "SELECT job_id FROM job_tokens WHERE token_hash = @hash AND job_id = @jobId AND expires_at > now()",
            new { hash = Hash(token), jobId });
        return ok ?? throw new Auth.ForbiddenException("Invalid or expired job token.");
    }

    public async Task AppendLogAsync(long jobId, int? stepNumber, IReadOnlyList<string> lines, CancellationToken ct)
    {
        if (lines.Count == 0) return;
        await using var conn = await db.OpenAsync(ct);
        var seq = await conn.ExecuteScalarAsync<long>("SELECT COALESCE(MAX(seq), 0) FROM job_logs WHERE job_id = @jobId", new { jobId });
        foreach (var line in lines)
        {
            seq++;
            await conn.ExecuteAsync("INSERT INTO job_logs (job_id, step_number, seq, line) VALUES (@jobId, @stepNumber, @seq, @line)",
                new { jobId, stepNumber, seq, line });
        }
    }

    public async Task UpdateStepAsync(long jobId, int number, string status, string? conclusion, int? exitCode, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("""
            UPDATE job_steps SET status = @status, conclusion = @conclusion, exit_code = @exitCode,
                started_at = COALESCE(started_at, CASE WHEN @status = 'running' THEN now() END),
                completed_at = CASE WHEN @status = 'completed' THEN now() END
            WHERE job_id = @jobId AND number = @number
            """, new { jobId, number, status, conclusion, exitCode });
    }

    public async Task CompleteJobAsync(long jobId, string conclusion, CancellationToken ct)
    {
        await using var conn = await db.OpenAsync(ct);
        await conn.ExecuteAsync("UPDATE workflow_jobs SET status = 'completed', conclusion = @conclusion, completed_at = now() WHERE id = @jobId",
            new { jobId, conclusion });
        await conn.ExecuteAsync("DELETE FROM job_tokens WHERE job_id = @jobId", new { jobId });

        var run = await conn.QuerySingleAsync<RunRow>("""
            SELECT r.id, r.workflow_path AS WorkflowPath, r.event, r.ref, r.head_sha AS HeadSha, r.pr_number AS PrNumber,
                   r.repo_id AS RepoId, r.workflow_name AS WorkflowName
            FROM workflow_runs r JOIN workflow_jobs j ON j.run_id = r.id WHERE j.id = @jobId
            """, new { jobId });

        var jobName = await conn.ExecuteScalarAsync<string>("SELECT name FROM workflow_jobs WHERE id = @jobId", new { jobId });
        await checks.SetAsync(run.RepoId, run.HeadSha, $"{run.WorkflowName} / {jobName}",
            conclusion == "success" ? "success" : "failure", conclusion == "success" ? "Passed" : "Failed", "", ct);

        var remaining = await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM workflow_jobs WHERE run_id = @runId AND status <> 'completed'", new { runId = run.Id });
        if (remaining == 0)
        {
            var anyFail = await conn.ExecuteScalarAsync<bool>("SELECT bool_or(conclusion <> 'success') FROM workflow_jobs WHERE run_id = @runId", new { runId = run.Id });
            var runConclusion = anyFail ? "failure" : "success";
            await conn.ExecuteAsync("UPDATE workflow_runs SET status = 'completed', conclusion = @c, completed_at = now() WHERE id = @id",
                new { c = runConclusion, id = run.Id });

            // If this run's head sha belongs to an open PR, surface the CI verdict to Enklr.
            if (run.PrNumber is { } prNum)
            {
                var orgSlug = await conn.QuerySingleAsync<string>(
                    "SELECT o.slug FROM repositories r JOIN organisations o ON o.id = r.org_id WHERE r.id = @repoId", new { run.RepoId });
                var orgId = await conn.ExecuteScalarAsync<long>("SELECT org_id FROM repositories WHERE id = @repoId", new { run.RepoId });
                var repoSlug = await conn.ExecuteScalarAsync<string>("SELECT slug FROM repositories WHERE id = @repoId", new { run.RepoId });
                var baseUrl = config["App:PublicBaseUrl"] ?? "http://localhost:8080";
                await enklr.UpdateStateAsync(orgId, "pull", $"#{prNum}", runConclusion,
                    $"{baseUrl}/#/o/{orgSlug}/{repoSlug}/pulls/{prNum}", ct);
            }
        }
    }

    // ---- helpers ----

    private static IEnumerable<Dictionary<string, string>> ExpandMatrix(IReadOnlyDictionary<string, IReadOnlyList<string>> matrix)
    {
        if (matrix.Count == 0) { yield return []; yield break; }
        var keys = matrix.Keys.ToList();
        var combos = new List<Dictionary<string, string>> { new() };
        foreach (var key in keys)
        {
            var next = new List<Dictionary<string, string>>();
            foreach (var c in combos)
                foreach (var val in matrix[key])
                    next.Add(new Dictionary<string, string>(c) { [key] = val });
            combos = next;
        }
        foreach (var c in combos) yield return c;
    }

    private static Dictionary<string, string> MergeEnv(Workflow wf, WorkflowJob job, WorkflowStep step)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in job.Env) d[k] = v;
        foreach (var (k, v) in step.Env) d[k] = v;
        return d;
    }

    private static bool GlobMatch(string pattern, string value)
    {
        if (pattern == value) return true;
        if (!pattern.Contains('*')) return false;
        var rx = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*\\*", ".*").Replace("\\*", "[^/]*") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, rx);
    }

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();
    private static string Hash(string s) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)));

    private static async Task<string> JobName(System.Data.IDbConnection conn, long jobId, System.Data.IDbTransaction tx)
        => await conn.ExecuteScalarAsync<string>("SELECT name FROM workflow_jobs WHERE id = @jobId", new { jobId }, tx) ?? "";

    private sealed record JobRow(long Id, string Name, string RunsOn, string Status, string? Conclusion, DateTime? StartedAt, DateTime? CompletedAt);
    private sealed record RunRow(long Id, string WorkflowPath, string Event, string Ref, string HeadSha, int? PrNumber, long RepoId, string WorkflowName = "");
    private sealed record ClaimRow(long JobId, long RunId, string MatrixJson, string RunsOn, int RunNumber, string HeadSha, long RepoId, string RepoSlug, string OrgSlug, long OrgId);
    private sealed record StepSpec(string? Name, string? Run, string? Uses, Dictionary<string, string>? With, Dictionary<string, string>? Env, string? If, string? Shell, string? WorkingDirectory, bool ContinueOnError);
}
