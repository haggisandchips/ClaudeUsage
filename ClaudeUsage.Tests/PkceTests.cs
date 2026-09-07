using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ClaudeUsage.Services;

namespace ClaudeUsage.Tests;

public class PkceTests
{
    [Fact]
    public void GenerateCodeVerifier_IsUrlSafe()
    {
        var verifier = Pkce.GenerateCodeVerifier();

        Assert.Matches(new Regex("^[A-Za-z0-9_-]+$"), verifier);
        Assert.DoesNotContain("=", verifier);
    }

    [Fact]
    public void GenerateCodeVerifier_IsDifferentEachCall()
    {
        var a = Pkce.GenerateCodeVerifier();
        var b = Pkce.GenerateCodeVerifier();

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void ComputeChallenge_IsBase64UrlSha256OfVerifier()
    {
        const string verifier = "example-code-verifier-value";

        var challenge = Pkce.ComputeChallenge(verifier);

        var expectedHash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        var expected = Convert.ToBase64String(expectedHash).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        Assert.Equal(expected, challenge);
        Assert.DoesNotContain("=", challenge);
        Assert.DoesNotContain("+", challenge);
        Assert.DoesNotContain("/", challenge);
    }
}
