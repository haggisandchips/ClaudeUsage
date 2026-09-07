using System;
using System.Text.Json.Serialization;

namespace ClaudeUsage.Models;

/// <summary>Raw response body from the OAuth token endpoint.</summary>
public sealed class TokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; set; }

    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("scope")]
    public string? Scope { get; set; }
}

/// <summary>Persisted token state, including the derived absolute expiry.</summary>
public sealed class TokenSet
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTimeOffset ExpiresAt { get; set; }

    public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt - TimeSpan.FromSeconds(30);

    public static TokenSet FromResponse(TokenResponse response) => new()
    {
        AccessToken = response.AccessToken,
        RefreshToken = response.RefreshToken,
        ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn)
    };
}
