using System.Text;
using GitSrv.Api.Auth;
using LibGit2Sharp;
using NotFoundException = GitSrv.Api.Auth.NotFoundException;
using ValidationException = GitSrv.Api.Auth.ValidationException;

namespace GitSrv.Api.Git;

// ---- read-model DTOs ----

public sealed record RefInfo(string Name, string Sha, bool IsDefault);
public sealed record RefsView(string? DefaultBranch, IReadOnlyList<RefInfo> Branches, IReadOnlyList<RefInfo> Tags, bool IsEmpty);

public sealed record PersonStamp(string Name, string Email, DateTimeOffset When);
public sealed record CommitSummary(string Sha, string ShortSha, string Summary, string Message,
    PersonStamp Author, PersonStamp Committer, IReadOnlyList<string> Parents);

public sealed record TreeEntryView(string Name, string Path, string Type, string Mode, long Size);
public sealed record TreeView(string Ref, string Path, CommitSummary? Commit, IReadOnlyList<TreeEntryView> Entries);

public sealed record BlobView(string Path, long Size, bool IsBinary, bool IsTruncated, string? Text, string Sha);

public sealed record DiffFile(string Path, string? OldPath, string ChangeKind, int Added, int Deleted, bool IsBinary, string? Patch);
public sealed record CommitDetail(CommitSummary Commit, IReadOnlyList<DiffFile> Files, int TotalAdded, int TotalDeleted);

public sealed record BlameHunk(int StartLine, int LineCount, string Sha, string ShortSha, PersonStamp Author, string Summary);
public sealed record BlameView(string Path, IReadOnlyList<string> Lines, IReadOnlyList<BlameHunk> Hunks);

public sealed record GraphCommit(string Sha, string ShortSha, string Summary, PersonStamp Author,
    IReadOnlyList<string> Parents, int Lane, IReadOnlyList<int> ParentLanes, IReadOnlyList<string> Refs);

public sealed record Comparison(
    string BaseRef, string HeadRef, string BaseSha, string HeadSha, string? MergeBaseSha,
    int Ahead, int Behind, bool Mergeable, IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<CommitSummary> Commits, IReadOnlyList<DiffFile> Files, int TotalAdded, int TotalDeleted);

/// <summary>
/// libgit2-backed read access to one bare repository. Cheap to construct; dispose per request.
/// Everything here is read-only and safe for anonymous callers once the caller has cleared the
/// repo's visibility check.
/// </summary>
public sealed class RepoReader : IDisposable
{
    public const long MaxTextBytes = 5 * 1024 * 1024;

    private readonly Repository _repo;

    public RepoReader(string repoDir)
    {
        if (!Repository.IsValid(repoDir))
            throw new NotFoundException("Repository is not initialised.");
        _repo = new Repository(repoDir);
    }

    public bool IsEmpty => _repo.Head?.Tip is null && !_repo.Branches.Any();

    public RefsView Refs()
    {
        var head = _repo.Head?.FriendlyName;
        var branches = _repo.Branches
            .Where(b => !b.IsRemote)
            .Select(b => new RefInfo(b.FriendlyName, b.Tip?.Sha ?? "", b.FriendlyName == head))
            .OrderByDescending(b => b.IsDefault)
            .ThenBy(b => b.Name, StringComparer.Ordinal)
            .ToList();
        var tags = _repo.Tags
            .Select(t => new RefInfo(t.FriendlyName, t.Target.Sha, false))
            .OrderByDescending(t => t.Name, StringComparer.Ordinal)
            .ToList();
        return new RefsView(head, branches, tags, IsEmpty);
    }

    public string? DefaultBranch => _repo.Head?.FriendlyName;

    private Commit ResolveCommit(string refOrSha)
    {
        var obj = _repo.Lookup<Commit>(refOrSha)
            ?? (_repo.Branches[refOrSha]?.Tip)
            ?? (_repo.Tags[refOrSha]?.PeeledTarget as Commit)
            ?? throw new NotFoundException($"No ref or commit '{refOrSha}'.");
        return obj;
    }

    public TreeView Tree(string refOrSha, string path)
    {
        var commit = ResolveCommit(refOrSha);
        Tree tree = commit.Tree;
        if (!string.IsNullOrEmpty(path))
        {
            var entry = commit[path] ?? throw new NotFoundException($"Path '{path}' not found.");
            if (entry.TargetType != TreeEntryTargetType.Tree)
                throw new NotFoundException($"'{path}' is not a directory.");
            tree = (Tree)entry.Target;
        }

        var entries = tree.Select(e =>
        {
            var type = e.TargetType switch
            {
                TreeEntryTargetType.Tree => "tree",
                TreeEntryTargetType.GitLink => "submodule",
                _ => "blob",
            };
            long size = e.TargetType == TreeEntryTargetType.Blob ? ((Blob)e.Target).Size : 0;
            var full = string.IsNullOrEmpty(path) ? e.Name : $"{path}/{e.Name}";
            return new TreeEntryView(e.Name, full, type, e.Mode.ToString(), size);
        })
        .OrderByDescending(e => e.Type == "tree")
        .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

        return new TreeView(refOrSha, path, Summarise(commit), entries);
    }

    public BlobView Blob(string refOrSha, string path)
    {
        var commit = ResolveCommit(refOrSha);
        var entry = commit[path] ?? throw new NotFoundException($"File '{path}' not found.");
        if (entry.TargetType != TreeEntryTargetType.Blob)
            throw new NotFoundException($"'{path}' is not a file.");

        var blob = (Blob)entry.Target;
        var isBinary = blob.IsBinary || LooksBinary(blob);
        if (isBinary)
            return new BlobView(path, blob.Size, true, false, null, blob.Sha);

        var truncated = blob.Size > MaxTextBytes;
        string? text = null;
        if (!truncated)
        {
            using var content = blob.GetContentStream();
            using var reader = new StreamReader(content, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        return new BlobView(path, blob.Size, false, truncated, text, blob.Sha);
    }

    public (Stream Content, long Size, string FileName) RawBlob(string refOrSha, string path)
    {
        var commit = ResolveCommit(refOrSha);
        var entry = commit[path] ?? throw new NotFoundException($"File '{path}' not found.");
        if (entry.TargetType != TreeEntryTargetType.Blob)
            throw new NotFoundException("Not a file.");
        var blob = (Blob)entry.Target;
        return (blob.GetContentStream(), blob.Size, Path.GetFileName(path));
    }

    public IReadOnlyList<CommitSummary> Log(string refOrSha, string? path, int skip, int take)
    {
        var start = ResolveCommit(refOrSha);
        var filter = new CommitFilter { IncludeReachableFrom = start, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        IEnumerable<Commit> commits = _repo.Commits.QueryBy(filter);

        if (!string.IsNullOrEmpty(path))
            commits = _repo.Commits.QueryBy(path, new CommitFilter { IncludeReachableFrom = start }).Select(le => le.Commit);

        return commits.Skip(skip).Take(take).Select(Summarise).ToList();
    }

    public CommitDetail Commit(string sha)
    {
        var commit = _repo.Lookup<Commit>(sha) ?? throw new NotFoundException($"No commit '{sha}'.");
        var parent = commit.Parents.FirstOrDefault();
        var changes = _repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree);

        var files = new List<DiffFile>();
        int totalAdd = 0, totalDel = 0;
        foreach (var pc in changes)
        {
            totalAdd += pc.LinesAdded;
            totalDel += pc.LinesDeleted;
            files.Add(new DiffFile(
                pc.Path, pc.OldPath == pc.Path ? null : pc.OldPath,
                pc.Status.ToString(), pc.LinesAdded, pc.LinesDeleted, pc.IsBinaryComparison,
                pc.IsBinaryComparison ? null : pc.Patch));
        }
        return new CommitDetail(Summarise(commit), files, totalAdd, totalDel);
    }

    public BlameView Blame(string refOrSha, string path)
    {
        var commit = ResolveCommit(refOrSha);
        var entry = commit[path] ?? throw new NotFoundException($"File '{path}' not found.");
        if (entry.TargetType != TreeEntryTargetType.Blob)
            throw new NotFoundException("Not a file.");
        var blob = (Blob)entry.Target;
        if (blob.IsBinary || blob.Size > MaxTextBytes)
            throw new ValidationException("Blame is not available for binary or very large files.");

        var lines = blob.GetContentText().Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];

        var hunks = _repo.Blame(path, new BlameOptions { StartingAt = commit })
            .Select(h => new BlameHunk(
                h.FinalStartLineNumber + 1, h.LineCount, h.FinalCommit.Sha, h.FinalCommit.Sha[..7],
                new PersonStamp(h.FinalCommit.Author.Name, h.FinalCommit.Author.Email, h.FinalCommit.Author.When),
                h.FinalCommit.MessageShort))
            .ToList();

        return new BlameView(path, lines, hunks);
    }

    public IReadOnlyList<GraphCommit> Graph(int limit)
    {
        if (IsEmpty) return [];

        var tips = _repo.Branches.Where(b => !b.IsRemote && b.Tip is not null).Select(b => b.Tip).Distinct().ToList();
        var refsBySha = new Dictionary<string, List<string>>();
        foreach (var b in _repo.Branches.Where(b => !b.IsRemote && b.Tip is not null))
            (refsBySha.TryGetValue(b.Tip!.Sha, out var l) ? l : refsBySha[b.Tip.Sha] = []).Add(b.FriendlyName);
        foreach (var t in _repo.Tags)
            (refsBySha.TryGetValue(t.Target.Sha, out var l) ? l : refsBySha[t.Target.Sha] = []).Add("tag: " + t.FriendlyName);

        var filter = new CommitFilter { IncludeReachableFrom = tips, SortBy = CommitSortStrategies.Topological | CommitSortStrategies.Time };
        var commits = _repo.Commits.QueryBy(filter).Take(limit).ToList();

        // Simple lane assignment: each active branch of history holds a lane; a commit takes the
        // lowest lane that expects it, its first parent inherits that lane, extra parents claim new lanes.
        var lanes = new List<string?>();        // lane -> sha it is currently waiting for
        var result = new List<GraphCommit>(commits.Count);

        foreach (var c in commits)
        {
            int lane = lanes.IndexOf(c.Sha);
            if (lane < 0)
            {
                lane = lanes.IndexOf(null);
                if (lane < 0) { lanes.Add(null); lane = lanes.Count - 1; }
            }

            var parents = c.Parents.Select(p => p.Sha).ToList();
            var parentLanes = new List<int>();
            if (parents.Count > 0)
            {
                lanes[lane] = parents[0];
                parentLanes.Add(lane);
                for (int i = 1; i < parents.Count; i++)
                {
                    int pl = lanes.IndexOf(parents[i]);
                    if (pl < 0)
                    {
                        pl = lanes.IndexOf(null);
                        if (pl < 0) { lanes.Add(parents[i]); pl = lanes.Count - 1; }
                        else lanes[pl] = parents[i];
                    }
                    parentLanes.Add(pl);
                }
            }
            else
            {
                lanes[lane] = null;
            }
            // free any lane still waiting on this commit (a merge target already placed)
            for (int i = 0; i < lanes.Count; i++)
                if (lanes[i] == c.Sha) lanes[i] = null;

            result.Add(new GraphCommit(
                c.Sha, c.Sha[..7], c.MessageShort,
                new PersonStamp(c.Author.Name, c.Author.Email, c.Author.When),
                parents, lane, parentLanes,
                refsBySha.TryGetValue(c.Sha, out var rs) ? rs : []));
        }
        return result;
    }

    public bool BranchExists(string name) => _repo.Branches[name] is not null;

    public string? Tip(string branch) => _repo.Branches[branch]?.Tip?.Sha;

    /// <summary>
    /// Three-way comparison of <paramref name="headRef"/> against <paramref name="baseRef"/>: the
    /// commits head adds since the merge base, the merge-base→head diff (what the PR changes), and
    /// a conflict-free mergeability check computed on trees only (no working directory).
    /// </summary>
    public Comparison Compare(string baseRef, string headRef)
    {
        var baseCommit = ResolveCommit(baseRef);
        var headCommit = ResolveCommit(headRef);
        var mergeBase = _repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit);

        var commits = mergeBase is null
            ? _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = headCommit }).ToList()
            : _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = headCommit, ExcludeReachableFrom = mergeBase }).ToList();

        int behind = mergeBase is null ? 0
            : _repo.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = baseCommit, ExcludeReachableFrom = headCommit }).Count();

        var diffTarget = mergeBase?.Tree ?? baseCommit.Tree;
        var patch = _repo.Diff.Compare<Patch>(diffTarget, headCommit.Tree,
            compareOptions: new CompareOptions { Similarity = SimilarityOptions.Renames });

        var files = new List<DiffFile>();
        int add = 0, del = 0;
        foreach (var pc in patch)
        {
            add += pc.LinesAdded;
            del += pc.LinesDeleted;
            files.Add(new DiffFile(pc.Path, pc.OldPath == pc.Path ? null : pc.OldPath,
                pc.Status.ToString(), pc.LinesAdded, pc.LinesDeleted, pc.IsBinaryComparison,
                pc.IsBinaryComparison ? null : pc.Patch));
        }

        bool mergeable = true;
        var conflicts = new List<string>();
        if (mergeBase is not null && baseCommit.Sha != headCommit.Sha)
        {
            var result = _repo.ObjectDatabase.MergeCommits(baseCommit, headCommit,
                new MergeTreeOptions { FailOnConflict = false });
            if (result.Status == MergeTreeStatus.Conflicts)
            {
                mergeable = false;
                conflicts = result.Conflicts.Select(c => c.Ours?.Path ?? c.Theirs?.Path ?? c.Ancestor?.Path ?? "?")
                    .Distinct().ToList();
            }
        }

        return new Comparison(baseRef, headRef, baseCommit.Sha, headCommit.Sha, mergeBase?.Sha,
            commits.Count, behind, mergeable, conflicts,
            commits.Select(Summarise).ToList(), files, add, del);
    }

    private static CommitSummary Summarise(Commit c) => new(
        c.Sha, c.Sha[..7], c.MessageShort, c.Message,
        new PersonStamp(c.Author.Name, c.Author.Email, c.Author.When),
        new PersonStamp(c.Committer.Name, c.Committer.Email, c.Committer.When),
        c.Parents.Select(p => p.Sha).ToList());

    private static bool LooksBinary(Blob blob)
    {
        using var s = blob.GetContentStream();
        Span<byte> buf = stackalloc byte[8000];
        int n = s.Read(buf);
        for (int i = 0; i < n; i++)
            if (buf[i] == 0) return true;
        return false;
    }

    public void Dispose() => _repo.Dispose();
}
