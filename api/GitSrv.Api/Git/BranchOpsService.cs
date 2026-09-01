using GitSrv.Api.Auth;
using LibGit2Sharp;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;
using Repository = LibGit2Sharp.Repository;

namespace GitSrv.Api.Git;

/// <summary>
/// Write operations on a bare repo done through libgit2, no working tree: branch create / rename /
/// delete, cherry-pick, revert, and single-file edit-and-commit. The pre-receive hook does not run
/// for these (they are not receive-pack), so callers must enforce permission + branch protection
/// themselves before invoking.
/// </summary>
public sealed class BranchOpsService(GitStorage storage, BranchProtectionService protections)
{
    public sealed record CommitResult(string Sha, string Branch);

    private Repository Open(long orgId, long repoId) => new(storage.RepoPath(orgId, repoId));

    public void CreateBranch(long orgId, long repoId, string name, string fromRef)
    {
        using var repo = Open(orgId, repoId);
        if (repo.Branches[name] is not null) throw new ValidationException($"Branch '{name}' already exists.");
        var target = repo.Lookup<Commit>(fromRef) ?? repo.Branches[fromRef]?.Tip ?? repo.Tags[fromRef]?.PeeledTarget as Commit
            ?? throw new NotFoundException($"No ref or commit '{fromRef}'.");
        repo.Branches.Add(name, target);
    }

    public async Task RenameBranchAsync(long orgId, long repoId, string from, string to, CancellationToken ct)
    {
        if (await protections.MatchAsync(repoId, from, ct) is { BlockDeletion: true })
            throw new ValidationException($"Branch '{from}' is protected and cannot be renamed.");
        using var repo = Open(orgId, repoId);
        var b = repo.Branches[from] ?? throw new NotFoundException($"No branch '{from}'.");
        if (repo.Branches[to] is not null) throw new ValidationException($"Branch '{to}' already exists.");
        repo.Branches.Rename(b, to);
    }

    public async Task DeleteBranchAsync(long orgId, long repoId, string name, string defaultBranch, CancellationToken ct)
    {
        if (name == defaultBranch) throw new ValidationException("You cannot delete the default branch.");
        if (await protections.MatchAsync(repoId, name, ct) is { BlockDeletion: true })
            throw new ValidationException($"Branch '{name}' is protected and cannot be deleted.");
        using var repo = Open(orgId, repoId);
        if (repo.Branches[name] is null) throw new NotFoundException($"No branch '{name}'.");
        repo.Branches.Remove(name);
    }

    public async Task<CommitResult> CherryPickAsync(long orgId, long repoId, string sha, string ontoBranch, Signature actor, CancellationToken ct)
        => await ApplyAsync(orgId, repoId, ontoBranch, actor, ct, (repo, onto) =>
        {
            var pick = repo.Lookup<Commit>(sha) ?? throw new NotFoundException($"No commit '{sha}'.");
            var res = repo.ObjectDatabase.CherryPickCommit(pick, onto, 0, new MergeTreeOptions { FailOnConflict = false });
            if (res.Status == MergeTreeStatus.Conflicts) throw new ValidationException("Cherry-pick conflicts with the target branch.");
            return repo.ObjectDatabase.CreateCommit(pick.Author, actor,
                $"{pick.MessageShort}\n\n(cherry picked from commit {pick.Sha})", res.Tree, [onto], prettifyMessage: true);
        });

    public async Task<CommitResult> RevertAsync(long orgId, long repoId, string sha, string ontoBranch, Signature actor, CancellationToken ct)
        => await ApplyAsync(orgId, repoId, ontoBranch, actor, ct, (repo, onto) =>
        {
            var target = repo.Lookup<Commit>(sha) ?? throw new NotFoundException($"No commit '{sha}'.");
            var res = repo.ObjectDatabase.RevertCommit(target, onto, 0, new MergeTreeOptions { FailOnConflict = false });
            if (res.Status == MergeTreeStatus.Conflicts) throw new ValidationException("Revert conflicts with the target branch.");
            return repo.ObjectDatabase.CreateCommit(actor, actor,
                $"Revert \"{target.MessageShort}\"\n\nThis reverts commit {target.Sha}.", res.Tree, [onto], prettifyMessage: true);
        });

    /// <summary>Edit or add a single file and commit it directly onto a branch.</summary>
    public async Task<CommitResult> EditFileAsync(long orgId, long repoId, string branch, string path, string content,
        string message, Signature actor, string? expectedBlobSha, CancellationToken ct)
        => await ApplyAsync(orgId, repoId, branch, actor, ct, (repo, onto) =>
        {
            var existing = onto[path];
            if (expectedBlobSha is not null && existing is not null && existing.Target.Sha != expectedBlobSha)
                throw new ValidationException("The file has changed since you started editing.");

            var blob = repo.ObjectDatabase.CreateBlob(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)));
            var def = TreeDefinition.From(onto.Tree).Add(path, blob, Mode.NonExecutableFile);
            var tree = repo.ObjectDatabase.CreateTree(def);
            if (tree.Sha == onto.Tree.Sha) throw new ValidationException("No changes to commit.");
            return repo.ObjectDatabase.CreateCommit(actor, actor,
                string.IsNullOrWhiteSpace(message) ? $"Update {path}" : message, tree, [onto], prettifyMessage: true);
        });

    public async Task<CommitResult> DeleteFileAsync(long orgId, long repoId, string branch, string path, string message, Signature actor, CancellationToken ct)
        => await ApplyAsync(orgId, repoId, branch, actor, ct, (repo, onto) =>
        {
            if (onto[path] is null) throw new NotFoundException($"'{path}' not found.");
            var def = TreeDefinition.From(onto.Tree).Remove(path);
            var tree = repo.ObjectDatabase.CreateTree(def);
            return repo.ObjectDatabase.CreateCommit(actor, actor,
                string.IsNullOrWhiteSpace(message) ? $"Delete {path}" : message, tree, [onto], prettifyMessage: true);
        });

    private async Task<CommitResult> ApplyAsync(long orgId, long repoId, string branch, Signature actor, CancellationToken ct,
        Func<Repository, Commit, Commit> makeCommit)
    {
        var rule = await protections.MatchAsync(repoId, branch, ct);
        if (rule is { RequirePullRequest: true })
            throw new ValidationException($"Branch '{branch}' is protected — commit via a pull request.");

        using var repo = Open(orgId, repoId);
        var b = repo.Branches[branch] ?? throw new NotFoundException($"No branch '{branch}'.");
        var commit = makeCommit(repo, b.Tip!);
        repo.Refs.UpdateTarget($"refs/heads/{branch}", commit.Sha);
        return new CommitResult(commit.Sha, branch);
    }
}
