using GitSrv.Api.Auth;
using LibGit2Sharp;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;

namespace GitSrv.Api.Git;

public sealed record MergeOutcome(string MergeSha, bool HeadBranchDeleted);

/// <summary>
/// Executes a pull-request merge on a bare repository entirely through libgit2 — merged tree,
/// synthesised commit(s), ref update — so no working directory or worktree is needed. Serialised
/// per repository by the caller.
/// </summary>
public sealed class PrMergeService
{
    public MergeOutcome Merge(string repoDir, string method, string baseBranch, string headBranch,
        string headSha, string title, int number, Signature actor, bool deleteHeadBranch)
    {
        using var repo = new Repository(repoDir);

        var baseRef = repo.Branches[baseBranch] ?? throw new ValidationException($"Base branch '{baseBranch}' no longer exists.");
        var head = repo.Lookup<Commit>(headSha) ?? throw new NotFoundException("Head commit not found.");
        var baseCommit = baseRef.Tip;

        var opts = new MergeTreeOptions { FailOnConflict = false };
        string resultSha = method switch
        {
            "merge" => MakeMerge(repo, baseCommit, head, opts, actor, MergeMessage(number, title, headBranch, baseBranch)),
            "squash" => MakeSquash(repo, baseCommit, head, opts, actor, SquashMessage(number, title)),
            "rebase" => MakeRebase(repo, baseCommit, head, opts, actor),
            _ => throw new ValidationException("Unknown merge method."),
        };

        repo.Refs.UpdateTarget($"refs/heads/{baseBranch}", resultSha);

        var deleted = false;
        if (deleteHeadBranch && repo.Branches[headBranch] is not null && headBranch != baseBranch)
        {
            repo.Branches.Remove(headBranch);
            deleted = true;
        }
        return new MergeOutcome(resultSha, deleted);
    }

    /// <summary>Whether a merge would apply cleanly, without changing anything.</summary>
    public bool CanMergeCleanly(string repoDir, string baseBranch, string headSha)
    {
        using var repo = new Repository(repoDir);
        var baseCommit = repo.Branches[baseBranch]?.Tip;
        var head = repo.Lookup<Commit>(headSha);
        if (baseCommit is null || head is null) return false;
        if (baseCommit.Sha == head.Sha) return false;
        var res = repo.ObjectDatabase.MergeCommits(baseCommit, head, new MergeTreeOptions { FailOnConflict = false });
        return res.Status != MergeTreeStatus.Conflicts;
    }

    private static string MakeMerge(Repository repo, Commit b, Commit h, MergeTreeOptions o, Signature s, string msg)
    {
        var res = repo.ObjectDatabase.MergeCommits(b, h, o);
        if (res.Status == MergeTreeStatus.Conflicts) throw new ValidationException("The branches have conflicts and cannot be merged automatically.");
        var commit = repo.ObjectDatabase.CreateCommit(s, s, msg, res.Tree, [b, h], prettifyMessage: true);
        return commit.Sha;
    }

    private static string MakeSquash(Repository repo, Commit b, Commit h, MergeTreeOptions o, Signature s, string msg)
    {
        var res = repo.ObjectDatabase.MergeCommits(b, h, o);
        if (res.Status == MergeTreeStatus.Conflicts) throw new ValidationException("The branches have conflicts and cannot be squash-merged.");
        var commit = repo.ObjectDatabase.CreateCommit(s, s, msg, res.Tree, [b], prettifyMessage: true);
        return commit.Sha;
    }

    private static string MakeRebase(Repository repo, Commit b, Commit h, MergeTreeOptions o, Signature committer)
    {
        var mergeBase = repo.ObjectDatabase.FindMergeBase(b, h)
            ?? throw new ValidationException("No common history to rebase onto.");
        var toReplay = repo.Commits
            .QueryBy(new CommitFilter { IncludeReachableFrom = h, ExcludeReachableFrom = mergeBase, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Reverse })
            .ToList();

        Commit onto = b;
        foreach (var c in toReplay)
        {
            if (c.Parents.Count() > 1) continue; // skip merges when linearising
            var res = repo.ObjectDatabase.CherryPickCommit(c, onto, 0, o);
            if (res.Status == MergeTreeStatus.Conflicts)
                throw new ValidationException($"Rebase hit a conflict at '{c.Sha[..7]} {c.MessageShort}'.");
            onto = repo.ObjectDatabase.CreateCommit(c.Author, committer, c.Message, res.Tree, [onto], prettifyMessage: false);
        }
        return onto.Sha;
    }

    private static string MergeMessage(int n, string title, string head, string @base)
        => $"Merge pull request #{n} from {head}\n\n{title}";
    private static string SquashMessage(int n, string title) => $"{title} (#{n})";
}
