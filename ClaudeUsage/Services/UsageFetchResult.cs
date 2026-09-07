using System;
using ClaudeUsage.Models;

namespace ClaudeUsage.Services;

internal enum UsageFetchStatus
{
    Success,
    NotSignedIn,
    AuthError,
    RateLimited,
    NetworkError
}

internal sealed class UsageFetchResult
{
    public UsageFetchStatus Status { get; init; }
    public UsageResponse? Usage { get; init; }
    public string? Message { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    public static UsageFetchResult Success(UsageResponse usage) =>
        new() { Status = UsageFetchStatus.Success, Usage = usage };

    public static UsageFetchResult NotSignedIn() =>
        new() { Status = UsageFetchStatus.NotSignedIn };

    public static UsageFetchResult AuthError(string message) =>
        new() { Status = UsageFetchStatus.AuthError, Message = message };

    public static UsageFetchResult RateLimited(TimeSpan? retryAfter) =>
        new() { Status = UsageFetchStatus.RateLimited, RetryAfter = retryAfter };

    public static UsageFetchResult NetworkError(string message) =>
        new() { Status = UsageFetchStatus.NetworkError, Message = message };
}
