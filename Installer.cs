// Installer.cs
//
// Downloads a product's zip, verifies SHA256, extracts to the install
// directory, resolves an exe to launch, and updates InstallState.
//
// Cooperative cancellation via CancellationToken. On cancel or failure,
// the temp zip is deleted; a partial install directory may be left
// behind but will be cleaned up on the next attempt (delete-then-extract).

using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace MartianGamesAlerts;

internal enum InstallStage
{
    Preparing,
    Downloading,
    Verifying,
    Extracting,
    Finalizing,
    Done,
    Failed,
}

internal sealed record InstallProgress(
    InstallStage Stage,
    long BytesDone,
    long BytesTotal,
    string? Message = null);

internal sealed class Installer
{
    private readonly Settings _settings;
    private readonly InstallState _state;

    public Installer(Settings settings, InstallState state)
    {
        _settings = settings;
        _state    = state;
    }

    public async Task InstallAsync(
        string manifestId,
        Product product,
        IProgress<InstallProgress> progress,
        CancellationToken ct)
    {
        var installRoot = _settings.GetInstallRoot();
        var installPath = Path.Combine(installRoot, manifestId);
        var tempZip     = Path.Combine(Path.GetTempPath(),
            $"mg-{manifestId}-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report(new InstallProgress(InstallStage.Preparing, 0, product.SizeBytes));
            Directory.CreateDirectory(installRoot);

            // 1. Download.
            await DownloadAsync(product.Url, tempZip, product.SizeBytes, progress, ct);

            // 2. Verify SHA256.
            progress.Report(new InstallProgress(InstallStage.Verifying, 0, 0));
            await VerifySha256Async(tempZip, product.Sha256, ct);

            // 3. Delete any prior install, then extract.
            progress.Report(new InstallProgress(InstallStage.Extracting, 0, 0));
            await DeleteDirectoryRobustAsync(installPath, ct);
            Directory.CreateDirectory(installPath);
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, installPath), ct);

            // 4. Resolve exe to launch.
            progress.Report(new InstallProgress(InstallStage.Finalizing, 0, 0));
            var exeName = FindGameExe(installPath, manifestId)
                ?? throw new InvalidOperationException(
                    $"No launchable executable found under {installPath}.");

            // 5. Persist state.
            _state.Products[manifestId] = new InstalledProduct
            {
                ManifestId  = manifestId,
                InstallPath = installPath,
                Sha256      = product.Sha256,
                ExeName     = exeName,
                SizeBytes   = product.SizeBytes,
                InstalledAt = DateTime.UtcNow,
            };
            _state.Save();

            progress.Report(new InstallProgress(InstallStage.Done, product.SizeBytes, product.SizeBytes));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private static async Task DownloadAsync(
        string url, string destPath, long expectedSize,
        IProgress<InstallProgress> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;
        using var download = await response.Content.ReadAsStreamAsync(ct);
        using var file     = File.Create(destPath);

        var buffer = new byte[81_920];
        long copied = 0;
        long lastReported = 0;
        int read;
        while ((read = await download.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;

            // Throttle UI updates: ~500 KB between reports keeps the
            // progress bar smooth without spamming the dispatcher.
            if (copied - lastReported >= 512_000 || copied == totalBytes)
            {
                progress.Report(new InstallProgress(
                    InstallStage.Downloading, copied, totalBytes));
                lastReported = copied;
            }
        }
    }

    private static async Task VerifySha256Async(
        string path, string expected, CancellationToken ct)
    {
        using var sha    = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash   = await sha.ComputeHashAsync(stream, ct);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"SHA256 mismatch. Expected {expected[..12]}…, got {actual[..12]}….");
    }

    // Resolves the exe to launch after extract. Searches the install root
    // first, then one level down — some zips wrap the whole game in a
    // top-level subdirectory whose name matches the product id (e.g.,
    // TankOff-Classic/TankOff-Classic.exe). Returned path is relative to
    // installPath and may contain a directory separator.
    private static string? FindGameExe(string installPath, string manifestId)
    {
        var candidates = new[]
        {
            manifestId + ".exe",
            manifestId.Replace("-", "")  + ".exe",
            manifestId.Replace("-", " ") + ".exe",
        };

        // 1. Convention hit at root.
        foreach (var candidate in candidates)
        {
            if (File.Exists(Path.Combine(installPath, candidate)))
                return candidate;
        }

        // 2. Convention hit one level down.
        foreach (var sub in Directory.GetDirectories(installPath))
        {
            foreach (var candidate in candidates)
            {
                if (File.Exists(Path.Combine(sub, candidate)))
                    return Path.Combine(Path.GetFileName(sub), candidate);
            }
        }

        // 3. Fallback: largest non-utility exe from root or any first-level
        //    subdirectory. Returned as a path relative to installPath.
        var searchDirs = new List<string> { installPath };
        searchDirs.AddRange(Directory.GetDirectories(installPath));

        var fallback = searchDirs
            .SelectMany(d => Directory.GetFiles(d, "*.exe", SearchOption.TopDirectoryOnly))
            .Select(p => new FileInfo(p))
            .Where(fi => !IsUtilityExe(fi.Name))
            .OrderByDescending(fi => fi.Length)
            .FirstOrDefault();

        return fallback == null
            ? null
            : Path.GetRelativePath(installPath, fallback.FullName);
    }

    private static bool IsUtilityExe(string fileName) =>
        fileName.StartsWith("UnityCrashHandler", StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("Uninstall.exe",         StringComparison.OrdinalIgnoreCase) ||
        fileName.Equals("UnityPlayer.exe",       StringComparison.OrdinalIgnoreCase);

    // Directory.Delete + recursive fails hard on the first sharing violation
    // or read-only file. This helper clears read-only attributes on the way
    // in, then retries with exponential backoff to ride out transient locks
    // (typically Windows Defender scanning a freshly-extracted tree).
    private static async Task DeleteDirectoryRobustAsync(string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attrs = File.GetAttributes(file);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
            }
        }
        catch
        {
            // Best-effort — if we can't enumerate/adjust attributes, the
            // Delete attempts below will surface the real problem.
        }

        const int maxAttempts = 6;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (
                (ex is IOException || ex is UnauthorizedAccessException)
                && attempt < maxAttempts)
            {
                // 250, 500, 1000, 2000, 4000 ms — total ~7.75s worst case.
                await Task.Delay(250 * (1 << (attempt - 1)), ct);
            }
        }
    }
}
