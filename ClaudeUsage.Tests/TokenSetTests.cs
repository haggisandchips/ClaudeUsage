using ClaudeUsage.Models;

namespace ClaudeUsage.Tests;

public class TokenSetTests
{
    [Fact]
    public void FromResponse_SetsExpiryFromExpiresIn()
    {
        var response = new TokenResponse
        {
            AccessToken = "access-123",
            RefreshToken = "refresh-456",
            ExpiresIn = 3600
        };

        var before = DateTimeOffset.UtcNow;
        var tokens = TokenSet.FromResponse(response);
        var after = DateTimeOffset.UtcNow;

        Assert.Equal("access-123", tokens.AccessToken);
        Assert.Equal("refresh-456", tokens.RefreshToken);
        Assert.InRange(tokens.ExpiresAt, before.AddSeconds(3600), after.AddSeconds(3600));
    }

    [Fact]
    public void IsExpired_False_WellBeforeExpiry()
    {
        var tokens = new TokenSet { ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

        Assert.False(tokens.IsExpired);
    }

    [Fact]
    public void IsExpired_True_AfterExpiry()
    {
        var tokens = new TokenSet { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

        Assert.True(tokens.IsExpired);
    }

    [Fact]
    public void IsExpired_True_WithinThirtySecondSafetyMargin()
    {
        // Treated as expired slightly early so a refresh has time to complete before a real 401.
        var tokens = new TokenSet { ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(10) };

        Assert.True(tokens.IsExpired);
    }
}
