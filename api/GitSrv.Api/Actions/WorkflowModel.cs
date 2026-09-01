namespace GitSrv.Api.Actions;

public sealed record WorkflowTriggers(bool Push, string[]? PushBranches, bool PullRequest, string[]? PrBranches);

public sealed record WorkflowStep(
    string? Name, string? Run, string? Uses,
    IReadOnlyDictionary<string, string> With, IReadOnlyDictionary<string, string> Env,
    string? If, string? Shell, string? WorkingDirectory, bool ContinueOnError);

public sealed record WorkflowJob(
    string Id, string Name, string RunsOn, string? Container,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Matrix,
    IReadOnlyList<string> Needs, IReadOnlyList<WorkflowStep> Steps,
    IReadOnlyDictionary<string, string> Env, string? If);

public sealed record Workflow(string Name, string Path, WorkflowTriggers On, IReadOnlyList<WorkflowJob> Jobs);
