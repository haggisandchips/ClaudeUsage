using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

/// <summary>
/// OAuth 2.0 PKCE login against Anthropic's console, reusing the Claude Code CLI's
/// public client id. Anthropic does not offer OAuth client registration for third-party
/// apps, and the fixed redirect URI is an Anthropic-hosted page (not a loopback we
/// control), so the flow is: open the browser -> user approves -> Anthropic shows a
/// short code -> user pastes it back into this app. This mirrors how the Claude Code
/// CLI itself performs a "manual" browser login.
///
/// This is unofficial and undocumented; Anthropic could change or revoke it at any time.
/// </summary>
internal sealed class OAuthService
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";
    private const string Scope = "org:create_api_key user:profile user:inference";

    private readonly HttpClient _http;
    private string? _codeVerifier;
    private string? _state;

    public OAuthService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>Generates a fresh PKCE pair and returns the URL to open in the browser.</summary>
    public string BeginLogin()
    {
        _codeVerifier = Pkce.GenerateCodeVerifier();
        _state = _codeVerifier;
        var challenge = Pkce.ComputeChallenge(_codeVerifier);

        var query = $"code=true" +
                    $"&client_id={Uri.EscapeDataString(ClientId)}" +
                    $"&response_type=code" +
                    $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                    $"&scope={Uri.EscapeDataString(Scope)}" +
                    $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                    $"&code_challenge_method=S256" +
                    $"&state={Uri.EscapeDataString(_state)}";

        return $"{AuthorizeUrl}?{query}";
    }

    /// <summary>
    /// Completes login using the code Anthropic's callback page displayed to the user.
    /// The pasted value is typically "{code}#{state}"; a bare code also works.
    /// </summary>
    public async Task<TokenSet> CompleteLoginAsync(string pastedCode, CancellationToken ct = default)
    {
        if (_codeVerifier is null || _state is null)
        {
            throw new InvalidOperationException("BeginLogin must be called before CompleteLoginAsync.");
        }

        var (code, state) = ParsePastedCode(pastedCode, _state);

        var body = new
        {
            grant_type = "authorization_code",
            code,
            state,
            client_id = ClientId,
            redirect_uri = RedirectUri,
            code_verifier = _codeVerifier
        };

        using var response = await _http.PostAsJsonAsync(TokenUrl, body, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty token response.");

        var tokens = TokenSet.FromResponse(token);
        TokenStore.Save(tokens);
        return tokens;
    }

    public async Task<TokenSet> RefreshAsync(TokenSet current, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException("No refresh token available; user must sign in again.");
        }

        var body = new
        {
            grant_type = "refresh_token",
            refresh_token = current.RefreshToken,
            client_id = ClientId
        };

        using var response = await _http.PostAsJsonAsync(TokenUrl, body, ct);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Empty token response.");

        // Some refresh responses omit refresh_token when it is unchanged.
        var refreshed = TokenSet.FromResponse(token);
        if (string.IsNullOrEmpty(refreshed.RefreshToken))
        {
            refreshed.RefreshToken = current.RefreshToken;
        }

        TokenStore.Save(refreshed);
        return refreshed;
    }

    /// <summary>
    /// Anthropic's callback page displays "{code}#{state}"; a bare code (no '#') also works,
    /// falling back to the state generated in <see cref="BeginLogin"/>.
    /// </summary>
    internal static (string Code, string State) ParsePastedCode(string pastedCode, string fallbackState)
    {
        var trimmed = pastedCode.Trim();
        var hashIndex = trimmed.IndexOf('#');
        return hashIndex >= 0
            ? (trimmed[..hashIndex], trimmed[(hashIndex + 1)..])
            : (trimmed, fallbackState);
    }
}
