using ClaudeUsage.Services;

namespace ClaudeUsage.Tests.TestSupport;

/// <summary>
/// Points AppPaths at a throwaway temp directory for the lifetime of a test, so tests never
/// read or write the developer's real %LOCALAPPDATA%\ClaudeUsage (which may hold a live session).
/// Tests using this must run serialized against each other - see the "AppData" collection.
/// </summary>
internal sealed class IsolatedAppData : IDisposable
{
    public string Directory { get; }

    public IsolatedAppData()
    {
        Directory = Path.Combine(Path.GetTempPath(), "ClaudeUsageTests_" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        AppPaths.DataDirectoryOverride = Directory;
    }

    public void Dispose()
    {
        AppPaths.DataDirectoryOverride = null;
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
