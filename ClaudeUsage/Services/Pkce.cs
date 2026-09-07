using System;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUsage.Services;

/// <summary>RFC 7636 PKCE helpers (code_verifier / code_challenge), split out for testability.</summary>
internal static class Pkce
{
    public static string GenerateCodeVerifier(int byteLength = 32) =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(byteLength));

    public static string ComputeChallenge(string codeVerifier) =>
        Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));

    public static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
