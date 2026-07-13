// UpdateChecker.cs
//
// Fetches the manifest and logs what's available. Logging goes through
// UpdateLog so this and SelfUpdater share one log file.

namespace MartianGamesAlerts;

internal static class UpdateChecker
{
    public static async Task CheckAsync(string manifestUrl, CancellationToken ct = default)
    {
        try
        {
            UpdateLog.Line("UPDATE", $"Fetching manifest from {manifestUrl}");
            using var client = new ManifestClient(manifestUrl);
            var manifest = await client.FetchAsync(ct);

            if (manifest == null)
            {
                UpdateLog.Line("UPDATE", "Manifest returned null (empty response?)");
                return;
            }

            UpdateLog.Line("UPDATE",
                $"OK — {manifest.Products.Count} product(s), {manifest.News.Count} news item(s)");

            foreach (var (id, p) in manifest.Products
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                UpdateLog.Line("UPDATE",
                    $"  {id,-22} {FormatBytes(p.SizeBytes),10}   " +
                    $"updated {p.UpdatedAt:yyyy-MM-dd}   sha256={ShaPrefix(p.Sha256)}");
            }

            foreach (var n in manifest.News)
            {
                UpdateLog.Line("UPDATE", $"  NEWS {n.PostedAt}: {n.Title}");
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Line("UPDATE", $"Check failed: {ex.GetType().Name}: {ex.Message}");
        }
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
