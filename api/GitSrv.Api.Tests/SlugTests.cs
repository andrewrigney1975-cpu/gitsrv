using GitSrv.Api.Identity;
using Xunit;

namespace GitSrv.Api.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("acme")]
    [InlineData("acme-corp")]
    [InlineData("acme_corp")]
    [InlineData("web3")]
    [InlineData("a1")]
    public void Accepts_valid_slugs(string s) => Assert.True(Slug.IsValid(s));

    [Theory]
    [InlineData("")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("Acme")]
    [InlineData("acme--corp")]
    [InlineData("acme__corp")]
    [InlineData("acme corp")]
    [InlineData("acme.corp")]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("settings")]
    public void Rejects_invalid_or_reserved_slugs(string s) => Assert.False(Slug.IsValid(s));

    [Fact]
    public void Rejects_slugs_over_max_length()
        => Assert.False(Slug.IsValid(new string('a', Slug.MaxLength + 1)));

    [Fact]
    public void Normalise_lowercases_and_trims()
        => Assert.Equal("acme", Slug.Normalise("  ACME  "));

    [Theory]
    [InlineData("Acme Corp", "acme-corp")]
    [InlineData("  My  Repo!!  ", "my-repo")]
    [InlineData("C# Toolkit", "c-toolkit")]
    public void Suggest_produces_a_usable_slug(string input, string expected)
        => Assert.Equal(expected, Slug.Suggest(input));
}
