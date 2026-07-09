// UpdateChecker.cs
//
// Fetches the manifest and logs what's available. Logs to BOTH stdout
// (which may or may not be visible depending on how the WinExe was
// launched) AND a rolling log file at
//   %APPDATA%\MartianGamesAlerts\update-check.log
// so we can always inspect what happened after the fact.
//
// No state tracking or version comparison yet — that gets added in step 9
// when we know what "installed version" means.

using System.Text;

namespace MartianGamesAlerts;

internal static class UpdateChecker
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MartianGamesAlerts", "update-check.log");

    private const long MaxLogBytes = 1_000_000;
    private static readonly object _logLock = new();

    public static async Task CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        RotateIfNeeded();
        try
        {
            Log($"Fetching manifest from {manifestUrl}");
            using var client = new ManifestClient(manifestUrl);
            var manifest = await client.FetchAsync(ct);

            if (manifest == null)
            {
                Log("Manifest returned null (empty response?)");
                return;
            }

            Log($"OK — {manifest.Products.Count} product(s), {manifest.News.Count} news item(s)");

            foreach (var (id, p) in manifest.Products.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                Log($"  {id,-22} {FormatBytes(p.SizeBytes),10}   updated {p.UpdatedAt:yyyy-MM-dd}   sha256={ShaPrefix(p.Sha256)}");
            }

            foreach (var n in manifest.News)
            {
                Log($"  NEWS {n.PostedAt}: {n.Title}");
            }
        }
        catch (Exception ex)
        {
            Log($"Check failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Log(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] [UPDATE] {message}";
        Console.WriteLine(line);
        lock (_logLock)
        {
            try
            {
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024L * 1024)        return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    private static string ShaPrefix(string sha) =>
        string.IsNullOrEmpty(sha) ? "(none)"
        : sha.Length > 16 ? sha[..16] + "…"
        : sha;
}