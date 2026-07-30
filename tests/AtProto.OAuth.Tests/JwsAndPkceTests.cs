using AtProto.OAuth;

namespace AtProto.OAuth.Tests;

public class JwsAndPkceTests
{
    [Theory]
    [InlineData("aaa.bbb.ccc", true)]
    [InlineData("aaa.bbb", false)]     // two segments
    [InlineData("aaa.bbb.ccc.ddd", false)] // four segments
    [InlineData(".bbb.ccc", false)]    // empty header
    [InlineData("aaa..ccc", false)]    // empty payload
    [InlineData("aaa.bbb.", false)]    // empty signature
    [InlineData("", false)]
    [InlineData(null, false)]
    public void JwsTryParse_ValidatesShape(string? token, bool expected)
    {
        Assert.Equal(expected, JwsParts.TryParse(token, out _));
    }

    [Fact]
    public void JwsTryParse_ExposesSegments()
    {
        Assert.True(JwsParts.TryParse("aaa.bbb.ccc", out JwsParts parts));
        Assert.Equal("aaa", parts.Header);
        Assert.Equal("bbb", parts.Payload);
        Assert.Equal("ccc", parts.Signature);
        Assert.Equal("aaa.bbb", parts.SigningInput);
    }

    [Fact]
    public void Pkce_GeneratedVerifier_IsValidAndRoundTrips()
    {
        string verifier = Pkce.GenerateVerifier();
        Assert.True(Pkce.IsValidVerifier(verifier));
        string challenge = Pkce.ComputeChallenge(verifier);
        Assert.True(Pkce.Verify(verifier, challenge));
    }

    [Theory]
    [InlineData("short", false)]                                   // < 43 chars
    [InlineData("has spaces in it has spaces in it has spaces!", false)] // illegal chars
    public void Pkce_IsValidVerifier_RejectsBadInput(string verifier, bool expected)
    {
        Assert.Equal(expected, Pkce.IsValidVerifier(verifier));
    }

    [Fact]
    public void Pkce_Verify_RejectsWrongVerifier()
    {
        string challenge = Pkce.ComputeChallenge(new string('a', 43));
        Assert.False(Pkce.Verify(new string('b', 43), challenge));
    }

    [Fact]
    public void Pkce_TooLongVerifier_IsInvalid()
    {
        Assert.False(Pkce.IsValidVerifier(new string('a', 129)));
        Assert.True(Pkce.IsValidVerifier(new string('a', 128)));
    }
}
