using GitSrv.Api.Domain;

namespace GitSrv.Api.Authz;

/// <summary>
/// All the facts about one principal's relationship to one repository, gathered from the database
/// by <see cref="Authorizer"/> and handed to the pure resolver below.
/// </summary>
public sealed record RepoAccessFacts
{
    public bool IsSiteAdmin { get; init; }

    /// <summary>The principal's role in the repo's owning org, or null if not a member.</summary>
    public OrgRole? OrgRole { get; init; }

    public string Visibility { get; init; } = "private";
    public bool IsArchived { get; init; }

    /// <summary>Direct collaborator grant, if any.</summary>
    public RepoPermission DirectGrant { get; init; } = RepoPermission.None;

    /// <summary>Grants via teams the principal belongs to.</summary>
    public IReadOnlyCollection<RepoPermission> TeamGrants { get; init; } = [];
}

/// <summary>
/// Pure, DB-free resolution of an effective repo permission. Unit-tested in isolation
/// (<c>PermissionResolverTests</c>); every DB-backed check in <see cref="Authorizer"/> funnels
/// through here so the policy lives in exactly one place.
/// </summary>
public static class PermissionResolver
{
    public static RepoPermission ResolveRepo(RepoAccessFacts f)
    {
        // Site admins hold admin on everything.
        if (f.IsSiteAdmin)
            return RepoPermission.Admin;

        var effective = RepoPermission.None;

        // Org role floor.
        if (f.OrgRole is { } role)
        {
            effective = role switch
            {
                // Owners and admins administer every repo in their org.
                Domain.OrgRole.Owner or Domain.OrgRole.Admin => RepoPermission.Admin,
                // Plain members get read on any repo that isn't private to them.
                Domain.OrgRole.Member when f.Visibility is "internal" or "public" => RepoPermission.Read,
                _ => RepoPermission.None,
            };
        }

        // Public repos are readable by anyone, member or not.
        if (f.Visibility == "public")
            effective = RepoPermissions.Max(effective, RepoPermission.Read);

        // Explicit grants stack on top and can only raise the effective level.
        effective = RepoPermissions.Max(effective, f.DirectGrant);
        foreach (var g in f.TeamGrants)
            effective = RepoPermissions.Max(effective, g);

        // An archived repo is read-only: clamp anything above Read down to Read, but never below
        // (an admin keeps enough to unarchive it — that's a separate settings check, still >= Read).
        if (f.IsArchived && effective > RepoPermission.Read)
            effective = f.OrgRole is Domain.OrgRole.Owner or Domain.OrgRole.Admin || f.DirectGrant == RepoPermission.Admin
                ? RepoPermission.Admin
                : RepoPermission.Read;

        return effective;
    }

    /// <summary>Can the principal read the repo (browse, clone, fetch)?</summary>
    public static bool CanRead(RepoAccessFacts f) => ResolveRepo(f) >= RepoPermission.Read;

    /// <summary>Can the principal push to the repo?</summary>
    public static bool CanWrite(RepoAccessFacts f) => ResolveRepo(f) >= RepoPermission.Write;

    /// <summary>Can the principal change repo settings / collaborators?</summary>
    public static bool CanAdmin(RepoAccessFacts f) => ResolveRepo(f) >= RepoPermission.Admin;
}
