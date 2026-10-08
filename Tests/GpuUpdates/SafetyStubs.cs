namespace Naufal_Windows_Tech_s_Powertoys;

// The real catalog/inventory code is linked, but installing/writing a package is
// impossible in this harness. These stubs fail closed if a test takes that path.
internal static class WindowsPrivilegeService { internal static bool IsAdministrator() => false; }
internal static class AppDataPaths { internal static string RuntimeCacheDirectory => throw new InvalidOperationException("No package cache in metadata tests."); }
internal readonly record struct SecuritySettingsLaunchResult(bool Success, string Message);
internal sealed record NativeCommandResult(int ExitCode, bool TimedOut, TimeSpan Duration, string CombinedOutput);
internal sealed class NativeCommandRunner
{
    internal Task<NativeCommandResult> RunAsync(string file, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token) =>
        throw new InvalidOperationException("Process execution is disabled in metadata tests.");
}
internal static class AtomicDownloadFile
{
    internal static Task<long> SaveAsync(Stream stream, string path, long? expected, long minimum, long maximum, Action<long> progress, CancellationToken token) =>
        throw new InvalidOperationException("Driver downloads are disabled in metadata tests.");
}
