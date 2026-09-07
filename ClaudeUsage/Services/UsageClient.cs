using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

/// <summary>
/// Owns the current auth session and fetches usage data from Anthropic's unofficial
/// OAuth usage endpoint (the same one Claude Code's own `/status` command reads from).
/// </summary>
internal sealed class UsageClient
{
    // Undocumented endpoint + beta header used by Claude Code to read rate-limit usage.
    // Both could change without notice - this is not a public/stable API.
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string AnthropicBetaHeader = "oauth-2025-04-20";

    // A claude-code-shaped User-Agent avoids a much more aggressive rate-limit bucket
    // applied to unrecognized clients.
    private const string UserAgent = "claude-code/2.0.1";

    private readonly HttpClient _http;
    private readonly OAuthService _oauth;

    public TokenSet? Tokens { get; private set; }
    public bool IsSignedIn => Tokens is not null;

    public event EventHandler? AuthChanged;

    public UsageClient(HttpClient http)
    {
        _http = http;
        _oauth = new OAuthService(http);
    }

    public void LoadPersistedSession()
    {
        Tokens = TokenStore.Load();
    }

    public string BeginLogin() => _oauth.BeginLogin();

    public async Task CompleteLoginAsync(string pastedCode, CancellationToken ct = default)
    {
        Tokens = await _oauth.CompleteLoginAsync(pastedCode, ct);
        AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SignOut()
    {
        TokenStore.Clear();
        Tokens = null;
        AuthChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<UsageFetchResult> FetchUsageAsync(CancellationToken ct = default)
    {
        if (Tokens is null)
        {
            return UsageFetchResult.NotSignedIn();
        }

        if (Tokens.IsExpired)
        {
            var refreshResult = await TryRefreshAsync(ct);
            if (refreshResult is not null)
            {
                return refreshResult;
            }
        }

        var result = await FetchOnceAsync(ct);

        if (result.Status == UsageFetchStatus.AuthError)
        {
            // Access token may have been revoked/expired server-side; try one refresh+retry.
            var refreshResult = await TryRefreshAsync(ct);
            if (refreshResult is not null)
            {
                return refreshResult;
            }

            result = await FetchOnceAsync(ct);
        }

        return result;
    }

    /// <summary>Returns null on success (caller should proceed to fetch), or a terminal result on failure.</summary>
    private async Task<UsageFetchResult?> TryRefreshAsync(CancellationToken ct)
    {
        try
        {
            Tokens = await _oauth.RefreshAsync(Tokens!, ct);
            AuthChanged?.Invoke(this, EventArgs.Empty);
            return null;
        }
        catch (Exception ex)
        {
            SignOut();
            return UsageFetchResult.AuthError($"Session expired, please sign in again ({ex.Message}).");
        }
    }

    private async Task<UsageFetchResult> FetchOnceAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Tokens!.AccessToken);
            request.Headers.Add("anthropic-beta", AnthropicBetaHeader);
            request.Headers.UserAgent.ParseAdd(UserAgent);

            using var response = await _http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return UsageFetchResult.AuthError("Unauthorized.");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                return UsageFetchResult.RateLimited(retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return UsageFetchResult.NetworkError($"HTTP {(int)response.StatusCode}");
            }

            var usage = await response.Content.ReadFromJsonAsync<UsageResponse>(cancellationToken: ct);
            if (usage is null)
            {
                return UsageFetchResult.NetworkError("Empty usage response.");
            }

            return UsageFetchResult.Success(usage);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return UsageFetchResult.NetworkError(ex.Message);
        }
    }
}
