namespace ClaudeUsage.Tests.TestSupport;

/// <summary>
/// AppPaths.DataDirectoryOverride is shared mutable static state, so any test that uses
/// IsolatedAppData must run serialized against every other such test (never in parallel).
/// </summary>
[CollectionDefinition("AppData", DisableParallelization = true)]
public sealed class AppDataCollection;
