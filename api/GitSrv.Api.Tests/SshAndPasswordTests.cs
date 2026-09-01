using GitSrv.Api.Auth;
using Xunit;

namespace GitSrv.Api.Tests;

public class SshPublicKeyTests
{
    // Throwaway keys generated with ssh-keygen for this test.
    private const string Ed25519 =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIACvYBWRsgBCyHWvn0wNglpkg2wEAe2FDiz5HPZ2ZMMU test@example";
    private const string Ed25519Fingerprint = "SHA256:JD2FuUGCGCUFAc9koVDYH5vNhSxMIkyoPFs13Os2VeI";

    [Fact]
    public void Parses_ed25519_and_computes_sha256_fingerprint()
    {
        Assert.True(SshPublicKey.TryParse(Ed25519, out var key, out var err), err);
        Assert.Equal("ssh-ed25519", key.KeyType);
        Assert.Equal(Ed25519Fingerprint, key.Fingerprint); // matches `ssh-keygen -lf`
        Assert.DoesNotContain("=", key.Fingerprint);        // unpadded
    }

    [Fact]
    public void Fingerprint_is_stable_regardless_of_comment()
    {
        SshPublicKey.TryParse(Ed25519, out var a, out _);
        SshPublicKey.TryParse(Ed25519.Replace(" test@example", " someone-else@host"), out var b, out _);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
    }

    [Theory]
    [InlineData("not a key")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 !!!notbase64!!!")]
    [InlineData("ssh-dss AAAAB3NzaC1kc3M= legacy")]
    public void Rejects_malformed_or_unsupported_keys(string input)
        => Assert.False(SshPublicKey.TryParse(input, out _, out _));

    [Fact]
    public void Rejects_key_whose_body_type_disagrees_with_prefix()
    {
        // ssh-rsa prefix but an ed25519 body.
        var swapped = "ssh-rsa AAAAC3NzaC1lZDI1NTE5AAAAIACvYBWRsgBCyHWvn0wNglpkg2wEAe2FDiz5HPZ2ZMMU x";
        Assert.False(SshPublicKey.TryParse(swapped, out _, out _));
    }
}

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_then_verify_round_trips()
    {
        var encoded = _hasher.Hash("correct horse battery staple");
        Assert.StartsWith("argon2id$", encoded);
        Assert.True(_hasher.Verify("correct horse battery staple", encoded));
    }

    [Fact]
    public void Verify_rejects_the_wrong_password()
        => Assert.False(_hasher.Verify("wrong", _hasher.Hash("right-and-long-enough")));

    [Fact]
    public void Each_hash_uses_a_fresh_salt()
        => Assert.NotEqual(_hasher.Hash("same-password-here"), _hasher.Hash("same-password-here"));

    [Fact]
    public void Verify_is_defensive_against_garbage_input()
        => Assert.False(_hasher.Verify("x", "not-a-valid-encoded-hash"));
}
