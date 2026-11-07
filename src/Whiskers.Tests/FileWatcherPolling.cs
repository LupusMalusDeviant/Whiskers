using System.Runtime.CompilerServices;

namespace Whiskers.Tests;

/// <summary>
/// Every booted test host watches its JSON config files (<c>reloadOnChange</c>), and on Linux each watcher holds an
/// inotify instance. The CI runner allows 128 per user, so a long run with many hosts hit that limit and every later
/// host failed to start ("The configured user limit (128) on the number of inotify instances has been reached").
/// Polling watchers hold no inotify instance. No test relies on a file change being noticed by itself: the tests that
/// need new settings call <c>IConfigurationRoot.Reload()</c>.
/// </summary>
internal static class FileWatcherPolling
{
    [ModuleInitializer]
    internal static void UsePollingFileWatchers() =>
        Environment.SetEnvironmentVariable("DOTNET_USE_POLLING_FILE_WATCHER", "true");
}
