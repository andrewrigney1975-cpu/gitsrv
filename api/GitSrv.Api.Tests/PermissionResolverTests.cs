using GitSrv.Api.Authz;
using GitSrv.Api.Domain;
using Xunit;

namespace GitSrv.Api.Tests;

public class PermissionResolverTests
{
    private static RepoAccessFacts Facts(
        bool siteAdmin = false,
        OrgRole? orgRole = null,
        string visibility = "private",
        bool archived = false,
        RepoPermission direct = RepoPermission.None,
        params RepoPermission[] teams) => new()
    {
        IsSiteAdmin = siteAdmin,
        OrgRole = orgRole,
        Visibility = visibility,
        IsArchived = archived,
        DirectGrant = direct,
        TeamGrants = teams,
    };

    [Fact]
    public void SiteAdmin_has_admin_everywhere()
    {
        Assert.Equal(RepoPermission.Admin, PermissionResolver.ResolveRepo(Facts(siteAdmin: true, visibility: "private")));
    }

    [Fact]
    public void Stranger_gets_nothing_on_private_repo()
    {
        Assert.Equal(RepoPermission.None, PermissionResolver.ResolveRepo(Facts(visibility: "private")));
    }

    [Fact]
    public void Anyone_can_read_a_public_repo()
    {
        Assert.Equal(RepoPermission.Read, PermissionResolver.ResolveRepo(Facts(visibility: "public")));
    }

    [Fact]
    public void Org_member_can_read_internal_but_not_private()
    {
        Assert.Equal(RepoPermission.Read, PermissionResolver.ResolveRepo(Facts(orgRole: OrgRole.Member, visibility: "internal")));
        Assert.Equal(RepoPermission.None, PermissionResolver.ResolveRepo(Facts(orgRole: OrgRole.Member, visibility: "private")));
    }

    [Theory]
    [InlineData(OrgRole.Owner)]
    [InlineData(OrgRole.Admin)]
    public void Org_owner_and_admin_administer_every_repo(OrgRole role)
    {
        Assert.Equal(RepoPermission.Admin, PermissionResolver.ResolveRepo(Facts(orgRole: role, visibility: "private")));
    }

    [Fact]
    public void Direct_collaborator_grant_raises_a_member_to_write()
    {
        var facts = Facts(orgRole: OrgRole.Member, visibility: "private", direct: RepoPermission.Write);
        Assert.Equal(RepoPermission.Write, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Effective_permission_is_the_max_across_grant_paths()
    {
        var facts = Facts(orgRole: OrgRole.Member, visibility: "internal",
            direct: RepoPermission.Triage, teams: new[] { RepoPermission.Write, RepoPermission.Read });
        Assert.Equal(RepoPermission.Write, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Grants_never_lower_an_existing_level()
    {
        var facts = Facts(orgRole: OrgRole.Admin, visibility: "private", direct: RepoPermission.Read);
        Assert.Equal(RepoPermission.Admin, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Archived_repo_is_read_only_for_writers()
    {
        var facts = Facts(orgRole: OrgRole.Member, visibility: "internal", archived: true, direct: RepoPermission.Write);
        Assert.Equal(RepoPermission.Read, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Archived_repo_keeps_admin_for_org_admin_so_it_can_be_unarchived()
    {
        var facts = Facts(orgRole: OrgRole.Admin, visibility: "private", archived: true);
        Assert.Equal(RepoPermission.Admin, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Archived_repo_keeps_admin_for_a_direct_admin_collaborator()
    {
        var facts = Facts(visibility: "private", archived: true, direct: RepoPermission.Admin);
        Assert.Equal(RepoPermission.Admin, PermissionResolver.ResolveRepo(facts));
    }

    [Fact]
    public void Helper_predicates_agree_with_resolved_level()
    {
        var writer = Facts(orgRole: OrgRole.Member, visibility: "internal", direct: RepoPermission.Write);
        Assert.True(PermissionResolver.CanRead(writer));
        Assert.True(PermissionResolver.CanWrite(writer));
        Assert.False(PermissionResolver.CanAdmin(writer));
    }
}
