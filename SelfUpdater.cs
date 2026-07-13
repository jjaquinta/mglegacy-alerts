// SelfUpdater.cs
//
// Two flows for keeping the launcher current:
//
//   BOOTSTRAP — when the running exe isn't at the expected install
//   location (%LOCALAPPDATA%\MartianGames\MartianGamesAlerts.exe), we
//   download the manifest version, install it there, launch it, and
//   let the current process exit. Triggered from Program.Main() before
//   any UI is up. Handles both "user downloaded exe from martiangames.com
//   and ran it from Downloads" and any other unofficial hand-off route.
//
//   IN-PLACE UPDATE — when the current exe IS at the expected location
//   but its SHA256 differs from the manifest. The rename shuffle:
//     current.exe  ->  current.exe.old   (Windows allows this on a
//                                          running exe; delete would fail)
//     new.exe      ->  current.exe       (via Move)
//     spawn(current.exe); exit
//   The outgoing process's mutex release plus the new process's WaitOne
//   timeout in Program.Main handle the single-instance handoff.
//
// Both flows share DownloadAndExtractAsync — fetch, SHA-verify, unzip.
//
// Dev-build skip: if the running exe's path contains \bin\ we treat this
// as a local build (dotnet run, or a local `dotnet publish`) and disable
// both flows so we don't clobber developer work.

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace MartianGamesAlerts;

internal enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,
    NotApplicable,     // dev build
    CannotDetermine,   // network / manifest / hash failure
}

internal static class SelfUpdater
{
    private const string ProductId = "MartianGamesAlerts";
    private const string ExeName   = "MartianGamesAlerts.exe";

    public static string ExpectedInstallPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MartianGames", ExeName);

    public static string CurrentExePath => Environment.ProcessPath
        ?? throw new InvalidOperationException("Environment.ProcessPath was null.");

    public static bool IsDevBuild() =>
        CurrentExePath.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase);

    public static bool IsRunningFromExpectedLocation() =>
        string.Equals(
            Path.GetFullPath(CurrentExePath),
            Path.GetFullPath(ExpectedInstallPath),
            StringComparison.OrdinalIgnoreCase);

    // Called at very start of Main. Removes any leftover .exe.old file
    // from a previous in-place update.
    public static void CleanupStaleFiles()
    {
        try
        {
            var oldExe = CurrentExePath + ".old";
            if (File.Exists(oldExe))
            {
                File.Delete(oldExe);
                UpdateLog.Line("SELF", $"Cleaned up {Path.GetFileName(oldExe)}");
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Line("SELF", $"Cleanup of .old failed (harmless): {ex.Message}");
        }
    }

    // If we're not at the expected install location, download the manifest
    // version, install it there, launch it, and return true. On any
    // failure (dev build, manifest unreachable, install location busy),
    // return false so Program.Main falls through to running in place.
    public static async Task<bool> TryBootstrapAsync(
        string manifestUrl, CancellationToken ct = default)
    {
        if (IsDevBuild())
        {
            UpdateLog.Line("SELF", $"Dev build detected ({CurrentExePath}); skipping bootstrap.");
            return false;
        }

        UpdateLog.Line("SELF", $"Bootstrapping install to {ExpectedInstallPath}");

        try
        {
            var product = await FetchProductAsync(manifestUrl, ct);
            if (product == null)
            {
                UpdateLog.Line("SELF", "Bootstrap aborted: manifest fetch failed.");
                return false;
            }

            var (tempDir, newExe) = await DownloadAndExtractAsync(product, ct);
            try
            {
                var installExe = ExpectedInstallPath;
                Directory.CreateDirectory(Path.GetDirectoryName(installExe)!);

                if (File.Exists(installExe))
                {
                    var oldExe = installExe + ".old";
                    try { if (File.Exists(oldExe)) File.Delete(oldExe); } catch { }
                    try
                    {
                        File.Move(installExe, oldExe);
                    }
                    catch (Exception ex)
                    {
                        // Existing install appears to be running with the
                        // mutex held. We can't overwrite; just launch it.
                        UpdateLog.Line("SELF",
                            $"Existing install seems to be running; launching it instead ({ex.Message})");
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = installExe,
                            UseShellExecute = false,
                        });
                        return true;
                    }
                }

                File.Move(newExe, installExe);
                UpdateLog.Line("SELF", $"Installed to {installExe}");
                SaveInstallState(installExe, product);

                Process.Start(new ProcessStartInfo
                {
                    FileName = installExe,
                    UseShellExecute = false,
                });
                return true;
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
        catch (Exception ex)
        {
            UpdateLog.Line("SELF", $"Bootstrap failed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // Compares the SHA256 recorded in install-state (from the last
    // bootstrap or in-place update) to what the manifest says. This is
    // like-to-like: both values are hashes of the same zip file. Hashing
    // the running exe and comparing to the manifest zip SHA would always
    // report a mismatch — those are two different files.
    public static async Task<UpdateStatus> CheckForUpdateAsync(
        string manifestUrl, CancellationToken ct = default)
    {
        if (IsDevBuild()) return UpdateStatus.NotApplicable;

        try
        {
            var product = await FetchProductAsync(manifestUrl, ct);
            if (product == null) return UpdateStatus.CannotDetermine;

            var state = InstallState.LoadOrCreate();
            if (!state.Products.TryGetValue(ProductId, out var installed))
            {
                UpdateLog.Line("SELF",
                    "No install-state entry for launcher; treating as update-available " +
                    "(state gets seeded by the next successful bootstrap/update).");
                return UpdateStatus.UpdateAvailable;
            }

            var same = string.Equals(installed.Sha256, product.Sha256,
                StringComparison.OrdinalIgnoreCase);
            UpdateLog.Line("SELF",
                $"Version check: installed-zip={installed.Sha256[..12]}…, " +
                $"manifest-zip={product.Sha256[..12]}… → {(same ? "up-to-date" : "update available")}");
            return same ? UpdateStatus.UpToDate : UpdateStatus.UpdateAvailable;
        }
        catch (Exception ex)
        {
            UpdateLog.Line("SELF", $"Check failed: {ex.GetType().Name}: {ex.Message}");
            return UpdateStatus.CannotDetermine;
        }
    }

    // Records the manifest zip SHA we just successfully installed from,
    // so the next CheckForUpdateAsync can compare zip-to-zip. Called from
    // both bootstrap and in-place update paths after the file is in
    // position and before the new instance is spawned.
    private static void SaveInstallState(string installedExePath, Product product)
    {
        try
        {
            var state = InstallState.LoadOrCreate();
            state.Products[ProductId] = new InstalledProduct
            {
                ManifestId  = ProductId,
                InstallPath = Path.GetDirectoryName(installedExePath) ?? "",
                ExeName     = Path.GetFileName(installedExePath),
                Sha256      = product.Sha256,
                SizeBytes   = product.SizeBytes,
                InstalledAt = DateTime.UtcNow,
            };
            state.Save();
            UpdateLog.Line("SELF",
                $"install-state updated: {ProductId} sha={product.Sha256[..12]}…");
        }
        catch (Exception ex)
        {
            UpdateLog.Line("SELF", $"install-state save failed: {ex.Message}");
        }
    }

    // Downloads the new version, does the rename shuffle, spawns the new
    // instance. Caller MUST exit the app on return so the new instance
    // can acquire the single-instance mutex.
    public static async Task DoInPlaceUpdateAsync(
        string manifestUrl, CancellationToken ct = default)
    {
        if (IsDevBuild())
            throw new InvalidOperationException("Cannot self-update a dev build.");

        var product = await FetchProductAsync(manifestUrl, ct)
            ?? throw new InvalidOperationException("Failed to fetch manifest.");

        var (tempDir, newExe) = await DownloadAndExtractAsync(product, ct);
        try
        {
            var currentExe = CurrentExePath;
            var oldExe     = currentExe + ".old";

            if (File.Exists(oldExe))
            {
                try { File.Delete(oldExe); }
                catch (Exception ex)
                {
                    UpdateLog.Line("SELF", $"Stale .old delete failed (proceeding): {ex.Message}");
                }
            }

            // Windows allows rename of a running exe (though not delete).
            File.Move(currentExe, oldExe);
            File.Move(newExe, currentExe);
            SaveInstallState(currentExe, product);

            UpdateLog.Line("SELF", $"Rename shuffle complete; spawning {currentExe}");
            Process.Start(new ProcessStartInfo
            {
                FileName = currentExe,
                UseShellExecute = false,
            });
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ---- helpers ----

    private static async Task<Product?> FetchProductAsync(
        string manifestUrl, CancellationToken ct)
    {
        using var client = new ManifestClient(manifestUrl);
        var manifest = await client.FetchAsync(ct);
        if (manifest == null) return null;
        return manifest.Products.TryGetValue(ProductId, out var product) ? product : null;
    }

    // Returns a temp directory (caller must delete) and the full path to
    // MartianGamesAlerts.exe extracted within it.
    private static async Task<(string TempDir, string ExeInTempDir)> DownloadAndExtractAsync(
        Product product, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mga-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var tempZip = Path.Combine(tempDir, "download.zip");

        try
        {
            UpdateLog.Line("SELF", $"Downloading {product.Url}");
            var handler = new HttpClientHandler
            {
                // Some server/CDN configs send Content-Encoding: gzip (or br)
                // regardless of Accept-Encoding. Without this, we'd write
                // still-compressed bytes to disk and fail SHA verification.
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            using (var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) })
            {
                using var response = await http.GetAsync(
                    product.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                using var file = File.Create(tempZip);
                await response.Content.CopyToAsync(file, ct);
            }

            UpdateLog.Line("SELF", "Verifying SHA256");
            string actualSha;
            using (var sha    = SHA256.Create())
            using (var stream = File.OpenRead(tempZip))
            {
                actualSha = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct))
                    .ToLowerInvariant();
            }
            if (!string.Equals(actualSha, product.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"SHA256 mismatch: expected {product.Sha256[..12]}…, got {actualSha[..12]}….");
            }

            UpdateLog.Line("SELF", "Extracting");
            ZipFile.ExtractToDirectory(tempZip, tempDir);

            var exeInTempDir = Path.Combine(tempDir, ExeName);
            if (!File.Exists(exeInTempDir))
                throw new InvalidOperationException($"Extracted zip missing {ExeName}.");

            return (tempDir, exeInTempDir);
        }
        catch
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
            throw;
        }
    }
}