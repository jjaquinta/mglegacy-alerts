// UpdateLog.cs
//
// Shared file logging for all update-related activity. Both UpdateChecker
// (manifest-content diagnostics) and SelfUpdater (bootstrap and in-place
// update flow) write here so the whole update story lives in one file:
//   %APPDATA%\MartianGamesAlerts\update-check.log
// Rotated (truncated) at ~1 MB.

using System.Text;

namespace MartianGamesAlerts;

internal static class UpdateLog
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MartianGamesAlerts", "update-check.log");

    private const long MaxLogBytes = 1_000_000;
    private static readonly object _lock = new();

    public static void Line(string tag, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [{tag}] {message}";
        Console.WriteLine(line);
        lock (_lock)
        {
            try
            {
                RotateIfNeeded();
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch { /* logging must never crash the caller */ }
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath)) return;
            if (new FileInfo(LogPath).Length > MaxLogBytes)
                File.Delete(LogPath);
        }
        catch { }
    }
}
