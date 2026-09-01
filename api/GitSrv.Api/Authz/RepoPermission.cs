namespace GitSrv.Api.Authz;

/// <summary>
/// Effective permission a principal holds on a repository, least to most privileged. The integer
/// order is load-bearing: resolution takes the maximum across every grant path.
/// </summary>
public enum RepoPermission
{
    None = 0,
    Read = 1,
    Triage = 2,
    Write = 3,
    Maintain = 4,
    Admin = 5,
}

public static class RepoPermissions
{
    public static RepoPermission Parse(string? value) => value switch
    {
        "read" => RepoPermission.Read,
        "triage" => RepoPermission.Triage,
        "write" => RepoPermission.Write,
        "maintain" => RepoPermission.Maintain,
        "admin" => RepoPermission.Admin,
        _ => RepoPermission.None,
    };

    public static string ToDbValue(RepoPermission p) => p switch
    {
        RepoPermission.Triage => "triage",
        RepoPermission.Write => "write",
        RepoPermission.Maintain => "maintain",
        RepoPermission.Admin => "admin",
        _ => "read",
    };

    public static RepoPermission Max(RepoPermission a, RepoPermission b) => a >= b ? a : b;
}
