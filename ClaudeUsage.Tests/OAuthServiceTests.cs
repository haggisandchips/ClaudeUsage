using System.Net;
using System.Web;
using ClaudeUsage.Services;
using ClaudeUsage.Tests.TestSupport;

namespace ClaudeUsage.Tests;

[Collection("AppData")]
public class OAuthServiceTests
{
    [Fact]
    public void BeginLogin_ReturnsAuthorizeUrlWithExpectedParams()
    {
        var oauth = new OAuthService(new HttpClient());

        var url = oauth.BeginLogin();

        var uri = new Uri(url);
        Assert.Equal("claude.ai", uri.Host);
        Assert.Equal("/oauth/authorize", uri.AbsolutePath);

        var query = HttpUtility.ParseQueryString(uri.Query);
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("https://console.anthropic.com/oauth/code/callback", query["redirect_uri"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(query["code_challenge"]));
        Assert.False(string.IsNullOrEmpty(query["state"]));
    }

    [Fact]
    public void BeginLogin_GeneratesFreshChallengeEachCall()
    {
        var oauth = new OAuthService(new HttpClient());

        var first = HttpUtility.ParseQueryString(new Uri(oauth.BeginLogin()).Query)["code_challenge"];
        var second = HttpUtility.ParseQueryString(new Uri(oauth.BeginLogin()).Query)["code_challenge"];

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("abc123#xyz789", "abc123", "xyz789")]
    [InlineData("  abc123#xyz789  ", "abc123", "xyz789")]
    [InlineData("justacode", "justacode", "fallback-state")]
    public void ParsePastedCode_SplitsOnHashOrFallsBackToState(string pasted, string expectedCode, string expectedState)
    {
        var (code, state) = OAuthService.ParsePastedCode(pasted, "fallback-state");

        Assert.Equal(expectedCode, code);
        Assert.Equal(expectedState, state);
    }

    [Fact]
    public async Task CompleteLoginAsync_Throws_WhenBeginLoginNotCalledFirst()
    {
        var oauth = new OAuthService(new HttpClient());

        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.CompleteLoginAsync("code#state"));
    }

    [Fact]
    public async Task CompleteLoginAsync_PostsAuthorizationCodeGrant_AndPersistsTokens()
    {
        using var isolated = new IsolatedAppData();

        CapturedRequest? tokenRequest = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            tokenRequest = req;
            var json = """{"access_token":"new-access","refresh_token":"new-refresh","expires_in":3600}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var oauth = new OAuthService(new HttpClient(handler));
        oauth.BeginLogin();

        var tokens = await oauth.CompleteLoginAsync("the-code#the-state");

        Assert.Equal("new-access", tokens.AccessToken);
        Assert.Equal("new-refresh", tokens.RefreshToken);

        Assert.NotNull(tokenRequest);
        Assert.Equal(new Uri("https://console.anthropic.com/v1/oauth/token"), tokenRequest!.Uri);
        Assert.Contains("\"grant_type\":\"authorization_code\"", tokenRequest.Body);
        Assert.Contains("\"code\":\"the-code\"", tokenRequest.Body);
        Assert.Contains("\"state\":\"the-state\"", tokenRequest.Body);

        var persisted = TokenStore.Load();
        Assert.NotNull(persisted);
        Assert.Equal("new-access", persisted!.AccessToken);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNoRefreshTokenPresent()
    {
        var oauth = new OAuthService(new HttpClient());
        var tokens = new Models.TokenSet { AccessToken = "a", RefreshToken = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => oauth.RefreshAsync(tokens));
    }

    [Fact]
    public async Task RefreshAsync_KeepsOldRefreshToken_WhenResponseOmitsIt()
    {
        using var isolated = new IsolatedAppData();

        var handler = new FakeHttpMessageHandler(_ =>
        {
            var json = """{"access_token":"rotated-access","expires_in":3600}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var oauth = new OAuthService(new HttpClient(handler));
        var current = new Models.TokenSet { AccessToken = "old-access", RefreshToken = "still-valid-refresh" };

        var refreshed = await oauth.RefreshAsync(current);

        Assert.Equal("rotated-access", refreshed.AccessToken);
        Assert.Equal("still-valid-refresh", refreshed.RefreshToken);
    }
}
