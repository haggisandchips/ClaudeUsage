using ClaudeUsage.Models;
using ClaudeUsage.Services;
using ClaudeUsage.Tests.TestSupport;

namespace ClaudeUsage.Tests;

[Collection("AppData")]
public class TokenStoreTests
{
    [Fact]
    public void Load_ReturnsNull_WhenNoFileExists()
    {
        using var isolated = new IsolatedAppData();

        Assert.Null(TokenStore.Load());
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsTokenSet()
    {
        using var isolated = new IsolatedAppData();

        var original = new TokenSet
        {
            AccessToken = "access-abc",
            RefreshToken = "refresh-xyz",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(2)
        };

        TokenStore.Save(original);
        var loaded = TokenStore.Load();

        Assert.NotNull(loaded);
        Assert.Equal(original.AccessToken, loaded!.AccessToken);
        Assert.Equal(original.RefreshToken, loaded.RefreshToken);
        Assert.Equal(original.ExpiresAt, loaded.ExpiresAt);
    }

    [Fact]
    public void Clear_RemovesPersistedFile()
    {
        using var isolated = new IsolatedAppData();

        TokenStore.Save(new TokenSet { AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
        Assert.NotNull(TokenStore.Load());

        TokenStore.Clear();

        Assert.Null(TokenStore.Load());
    }

    [Fact]
    public void Save_DoesNotStoreAccessTokenAsPlainText_OnWindows()
    {
        // vanilla xUnit v2 has no runtime-conditional skip; no-op on non-Windows instead.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var isolated = new IsolatedAppData();
        const string secret = "super-secret-access-token-value";

        TokenStore.Save(new TokenSet { AccessToken = secret, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var rawBytes = File.ReadAllBytes(AppPaths.CredentialsFile);
        var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);

        Assert.DoesNotContain(secret, rawText);
    }
}
