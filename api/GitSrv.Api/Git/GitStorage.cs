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
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var branch = string.IsNullOrWhiteSpace(defaultBranch) ? "main" : defaultBranch;

        await RunAsync("git", ["init", "--bare", $"--initial-branch={branch}", path], null, ct);
        // Allow partial-clone filters (harmless now, used from Phase 10) and keep gc quiet on push.
        await RunAsync("git", ["-C", path, "config", "uploadpack.allowFilter", "true"], null, ct);
        await RunAsync("git", ["-C", path, "config", "receive.denyNonFastForwards", "false"], null, ct);
        logger.LogInformation("Initialised bare repo {Path}", path);
    }

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
