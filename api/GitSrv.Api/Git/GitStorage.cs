using System.Diagnostics;

namespace GitSrv.Api.Git;

public sealed class GitStorageOptions
{
    /// <summary>Root directory holding all bare repositories. Shared volume with the ssh container.</summary>
    public string RepositoryRoot { get; init; } = "/var/lib/gitsrv/repositories";

    /// <summary>Reject a receive-pack whose request body exceeds this. 0 disables the check.</summary>
    public long MaxPushBytes { get; init; } = 512L * 1024 * 1024;
}

/// <summary>
/// Owns the on-disk bare repositories. Layout is <c>{root}/{orgId}/{repoId}.git</c> — keyed by id,
/// not slug, so org/repo renames never move a directory or race an in-flight push. The org id in
/// the path is purely for operator legibility (and lets an org delete be a single <c>rm -rf</c>).
/// </summary>
public sealed class GitStorage(GitStorageOptions options, ILogger<GitStorage> logger)
{
    public string RepositoryRoot => options.RepositoryRoot;
    public long MaxPushBytes => options.MaxPushBytes;

    public string RepoPath(long orgId, long repoId)
        => Path.Combine(options.RepositoryRoot, orgId.ToString(), $"{repoId}.git");

    public bool Exists(long orgId, long repoId)
        => Directory.Exists(RepoPath(orgId, repoId));

    /// <summary>Creates the bare repo if it does not already exist. Idempotent (self-heals records
    /// created before Phase 2, or after a volume restore).</summary>
    public async Task EnsureAsync(long orgId, long repoId, string defaultBranch, CancellationToken ct)
    {
        var path = RepoPath(orgId, repoId);
        if (Directory.Exists(path))
        {
            WriteHooks(path); // keep hook scripts current across upgrades
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var branch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch;

        await RunAsync("git", ["init", "--bare", $"--initial-branch={branch}", path], null, ct);
        // Allow partial-clone filters (harmless now, used from Phase 10) and keep gc quiet on push.
        await RunAsync("git", ["-C", path, "config", "uploadpack.allowFilter", "true"], null, ct);
        await RunAsync("git", ["-C", path, "config", "receive.denyNonFastForwards", "false"], null, ct);
        WriteHooks(path);
        logger.LogInformation("Initialised bare repo {Path}", path);
    }

    /// <summary>(Re)writes the pre-receive / post-receive hooks. Idempotent; call on ensure and on upgrade.</summary>
    public void WriteHooks(string repoDir)
    {
        var hooks = Path.Combine(repoDir, "hooks");
        Directory.CreateDirectory(hooks);
        WriteExecutable(Path.Combine(hooks, "pre-receive"), PreReceiveHook);
        WriteExecutable(Path.Combine(hooks, "post-receive"), PostReceiveHook);
    }

    public void EnsureHooks(long orgId, long repoId)
    {
        var path = RepoPath(orgId, repoId);
        if (Directory.Exists(path)) WriteHooks(path);
    }

    private static void WriteExecutable(string file, string contents)
    {
        File.WriteAllText(file, contents.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    // The hook process inherits GITSRV_API_BASE / GITSRV_INTERNAL_TOKEN / GITSRV_REPO_ID /
    // GITSRV_PUSHER_ID from whichever container ran receive-pack (api or ssh). No jq available in
    // the api image, so the endpoints speak line-oriented plain text.
    private const string PreReceiveHook = """
        #!/bin/sh
        updates=""
        while read -r old new ref; do
          updates="${updates}${old} ${new} ${ref}\n"
        done
        [ -z "$updates" ] && exit 0
        resp=$(printf "%b" "$updates" | curl -s -m 15 -X POST \
          "${GITSRV_API_BASE}/internal/hooks/pre-receive?repoId=${GITSRV_REPO_ID}&pusherId=${GITSRV_PUSHER_ID}" \
          -H "X-Internal-Token: ${GITSRV_INTERNAL_TOKEN}" -H 'Content-Type: text/plain' --data-binary @-)
        if [ $? -ne 0 ]; then echo "GitSrv: branch-policy service is unavailable; push rejected." >&2; exit 1; fi
        if [ "$(printf '%s' "$resp" | head -n 1)" = "allow" ]; then exit 0; fi
        printf '%s\n' "$resp" | tail -n +2 >&2
        exit 1
        """;

    private const string PostReceiveHook = """
        #!/bin/sh
        updates=""
        while read -r old new ref; do
          updates="${updates}${old} ${new} ${ref}\n"
        done
        printf "%b" "$updates" | curl -s -m 15 -X POST \
          "${GITSRV_API_BASE}/internal/hooks/post-receive?repoId=${GITSRV_REPO_ID}&pusherId=${GITSRV_PUSHER_ID}" \
          -H "X-Internal-Token: ${GITSRV_INTERNAL_TOKEN}" -H 'Content-Type: text/plain' --data-binary @- >/dev/null 2>&1 || true
        exit 0
        """;

    public void Delete(long orgId, long repoId)
    {
        var path = RepoPath(orgId, repoId);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            logger.LogInformation("Deleted bare repo {Path}", path);
        }
    }

    /// <summary>Recomputes on-disk size. Called after a successful push.</summary>
    public long MeasureSize(long orgId, long repoId)
    {
        var dir = new DirectoryInfo(RepoPath(orgId, repoId));
        if (!dir.Exists)
            return 0;
        long total = 0;
        foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try { total += f.Length; } catch { /* file vanished mid-walk */ }
        }
        return total;
    }

    private static async Task RunAsync(string file, string[] args, string? workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (workingDir is not null) psi.WorkingDirectory = workingDir;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {file}.");
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited {proc.ExitCode}: {stderr}");
    }
}
