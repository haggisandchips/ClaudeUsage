using System.Net;
using System.Text;
using ClaudeUsage.Models;
using ClaudeUsage.Services;
using ClaudeUsage.Tests.TestSupport;

namespace ClaudeUsage.Tests;

[Collection("AppData")]
public class UsageClientTests
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";

    private static HttpResponseMessage JsonResponse(HttpStatusCode status, string json) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    [Fact]
    public async Task FetchUsageAsync_ReturnsNotSignedIn_WhenNoTokens()
    {
        var client = new UsageClient(new HttpClient(new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("Should not make any HTTP calls when signed out."))));

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.NotSignedIn, result.Status);
    }

    [Fact]
    public async Task FetchUsageAsync_ReturnsSuccess_WithParsedUsageAndAuthHeaders()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet { AccessToken = "valid-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var handler = new FakeHttpMessageHandler(req =>
        {
            Assert.Equal(new Uri(UsageUrl), req.Uri);
            Assert.Equal("Bearer valid-token", req.Headers.Authorization?.ToString());
            Assert.Contains("oauth-2025-04-20", req.Headers.GetValues("anthropic-beta"));

            var json = """
                {
                  "five_hour": { "utilization": 42.0, "resets_at": "2026-01-01T00:00:00+00:00" },
                  "seven_day": { "utilization": 17.0, "resets_at": "2026-01-05T00:00:00+00:00" }
                }
                """;
            return JsonResponse(HttpStatusCode.OK, json);
        });

        var client = new UsageClient(new HttpClient(handler));
        client.LoadPersistedSession();

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.Success, result.Status);
        Assert.Equal(42.0, result.Usage!.FiveHour!.Utilization);
        Assert.Equal(17.0, result.Usage.SevenDay!.Utilization);
    }

    [Fact]
    public async Task FetchUsageAsync_ReturnsRateLimited_WithRetryAfterDelay()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet { AccessToken = "valid-token", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var handler = new FakeHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return response;
        });

        var client = new UsageClient(new HttpClient(handler));
        client.LoadPersistedSession();

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
    }

    [Fact]
    public async Task FetchUsageAsync_RefreshesExpiredTokenBeforeFetching()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet
        {
            AccessToken = "expired-token",
            RefreshToken = "my-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Uri == new Uri(TokenUrl))
            {
                Assert.Contains("\"grant_type\":\"refresh_token\"", req.Body);
                Assert.Contains("\"refresh_token\":\"my-refresh-token\"", req.Body);
                return JsonResponse(HttpStatusCode.OK,
                    """{"access_token":"refreshed-token","refresh_token":"my-refresh-token","expires_in":3600}""");
            }

            Assert.Equal("Bearer refreshed-token", req.Headers.Authorization?.ToString());
            return JsonResponse(HttpStatusCode.OK,
                """{"five_hour":{"utilization":5.0},"seven_day":{"utilization":6.0}}""");
        });

        var client = new UsageClient(new HttpClient(handler));
        client.LoadPersistedSession();

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.Success, result.Status);
        Assert.Equal("refreshed-token", client.Tokens!.AccessToken);
    }

    [Fact]
    public async Task FetchUsageAsync_RefreshesOn401_ThenRetriesAndSucceeds()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet
        {
            AccessToken = "stale-but-unexpired-token",
            RefreshToken = "my-refresh-token",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var usageCallCount = 0;
        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Uri == new Uri(TokenUrl))
            {
                return JsonResponse(HttpStatusCode.OK,
                    """{"access_token":"fresh-token","refresh_token":"my-refresh-token","expires_in":3600}""");
            }

            usageCallCount++;
            if (usageCallCount == 1)
            {
                Assert.Equal("Bearer stale-but-unexpired-token", req.Headers.Authorization?.ToString());
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            Assert.Equal("Bearer fresh-token", req.Headers.Authorization?.ToString());
            return JsonResponse(HttpStatusCode.OK,
                """{"five_hour":{"utilization":1.0},"seven_day":{"utilization":2.0}}""");
        });

        var client = new UsageClient(new HttpClient(handler));
        client.LoadPersistedSession();

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.Success, result.Status);
        Assert.Equal(2, usageCallCount);
    }

    [Fact]
    public async Task FetchUsageAsync_SignsOut_WhenRefreshFailsAfterAuthError()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet
        {
            AccessToken = "stale-token",
            RefreshToken = "no-longer-valid-refresh",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });

        var handler = new FakeHttpMessageHandler(req =>
        {
            if (req.Uri == new Uri(TokenUrl))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest); // e.g. invalid_grant
            }

            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        });

        var client = new UsageClient(new HttpClient(handler));
        client.LoadPersistedSession();

        var result = await client.FetchUsageAsync();

        Assert.Equal(UsageFetchStatus.AuthError, result.Status);
        Assert.False(client.IsSignedIn);
        Assert.Null(TokenStore.Load());
    }

    [Fact]
    public void SignOut_ClearsTokensAndRaisesAuthChanged()
    {
        using var isolated = new IsolatedAppData();
        TokenStore.Save(new TokenSet { AccessToken = "a", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });

        var client = new UsageClient(new HttpClient(new FakeHttpMessageHandler(_ =>
            throw new InvalidOperationException("SignOut should not make HTTP calls."))));
        client.LoadPersistedSession();
        Assert.True(client.IsSignedIn);

        var raised = false;
        client.AuthChanged += (_, _) => raised = true;

        client.SignOut();

        Assert.False(client.IsSignedIn);
        Assert.True(raised);
        Assert.Null(TokenStore.Load());
    }
}
