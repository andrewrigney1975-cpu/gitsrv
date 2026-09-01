namespace GitSrv.Api.Domain;

public sealed record User(
    long Id,
    string Username,
    string Email,
    string DisplayName,
    bool IsSiteAdmin,
    DateTime CreatedAt);

public sealed record Organisation(
    long Id,
    string Slug,
    string Name,
    string Description,
    long CreatedBy,
    DateTime CreatedAt);

public sealed record Team(
    long Id,
    long OrgId,
    string Slug,
    string Name,
    string Description,
    DateTime CreatedAt);

public sealed record Repository(
    long Id,
    long OrgId,
    string Slug,
    string Name,
    string Description,
    string Visibility,
    string DefaultBranch,
    bool IsArchived,
    long CreatedBy,
    DateTime CreatedAt);

public sealed record SshKey(
    long Id,
    long UserId,
    string Title,
    string KeyType,
    string Fingerprint,
    DateTime CreatedAt,
    DateTime? LastUsedAt);

/// <summary>Org membership roles, least to most privileged.</summary>
public enum OrgRole { Member = 0, Admin = 1, Owner = 2 }

public static class OrgRoles
{
    public const string Member = "member";
    public const string Admin = "admin";
    public const string Owner = "owner";

    public static OrgRole Parse(string value) => value switch
    {
        "owner" => OrgRole.Owner,
        "admin" => OrgRole.Admin,
        _ => OrgRole.Member,
    };
}
