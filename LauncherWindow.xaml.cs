// LauncherWindow.xaml.cs
//
// Cross-references the launcher manifest with settings-listed games and
// install-state, and renders one tile per game. Tile buttons dispatch to
// Install / Update / Cancel / Play based on the tile's current state.
//
// Each tile has an independent CancellationTokenSource for its in-flight
// install. Closing the window cancels every pending install.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

using Button     = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace MartianGamesAlerts;

public partial class LauncherWindow : Window
{
    private readonly Settings     _settings;
    private readonly InstallState _state;
    private readonly Installer    _installer;
    private Manifest?             _manifest;
    private readonly List<LauncherTile> _tiles = new();

    internal LauncherWindow(Settings settings)
    {
        _settings  = settings;
        _state     = InstallState.LoadOrCreate();
        _installer = new Installer(_settings, _state);
        InitializeComponent();
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading manifest…";
        GamesEmpty.Visibility = Visibility.Collapsed;
        NewsEmpty.Visibility  = Visibility.Collapsed;

        try
        {
            using var client = new ManifestClient(_settings.ManifestUrl);
            _manifest = await client.FetchAsync();
            if (_manifest == null)
            {
                StatusText.Text = "Manifest was empty.";
                return;
            }

            RenderTiles(_manifest);
            RenderNews(_manifest);
            StatusText.Text = $"Ready · {_manifest.Products.Count} product(s) in manifest";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Fetch failed: {ex.Message}";
        }
    }

    private void RenderTiles(Manifest manifest)
    {
        _tiles.Clear();
        foreach (var g in _settings.Games)
        {
            if (!g.Enabled) continue;
            if (string.IsNullOrEmpty(g.ManifestId)) continue;
            if (!manifest.Products.TryGetValue(g.ManifestId, out var product)) continue;

            var tile = new LauncherTile
            {
                DisplayName = g.DisplayName,
                ManifestId  = g.ManifestId,
                SizeText    = FormatBytes(product.SizeBytes),
                UpdatedText = $"Updated {product.UpdatedAt:yyyy-MM-dd}",
            };
            ApplyInstallState(tile, product);
            _tiles.Add(tile);
        }
        GamesList.ItemsSource = _tiles;
        GamesEmpty.Visibility = _tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Renders upcoming events. Rules:
    //   - Parse posted_at as UTC ("yyyy-MM-dd HH:mm:ss").
    //   - Skip anything in the past.
    //   - Skip anything whose Game (a ManifestId) doesn't correspond to
    //     an Enabled game in Settings. Events with no Game field are
    //     treated as global and always shown.
    //   - Sort soonest-first.
    //   - Match Game case-insensitively (the manifest may have typos).
    private void RenderNews(Manifest manifest)
    {
        var visibleGameIds = _settings.Games
            .Where(g => g.Enabled && !string.IsNullOrEmpty(g.ManifestId))
            .Select(g => g.ManifestId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var nowUtc = DateTime.UtcNow;

        var visible = manifest.News
            .Select(n => (Item: n, Utc: TryParseUtc(n.PostedAt)))
            .Where(x => x.Utc.HasValue && x.Utc.Value >= nowUtc)
            .Where(x => string.IsNullOrEmpty(x.Item.Game)
                        || visibleGameIds.Contains(x.Item.Game))
            .OrderBy(x => x.Utc)
            .Select(x => new EventVm
            {
                Title    = x.Item.Title,
                Body     = x.Item.Body,
                When     = FormatWhen(x.Utc!.Value),
                GameName = DisplayNameFor(x.Item.Game),
            })
            .ToList();

        if (visible.Count == 0)
        {
            NewsList.ItemsSource = null;
            NewsEmpty.Visibility = Visibility.Visible;
        }
        else
        {
            NewsList.ItemsSource = visible;
            NewsEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private static readonly string[] EventTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssZ",
    };

    private static DateTime? TryParseUtc(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return System.DateTime.TryParseExact(
                   s.Trim(),
                   EventTimeFormats,
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.AssumeUniversal
                   | System.Globalization.DateTimeStyles.AdjustToUniversal,
                   out var dt)
            ? dt
            : null;
    }

    private static string FormatWhen(DateTime utc)
    {
        var local    = utc.ToLocalTime();
        var relative = FormatRelative(utc - DateTime.UtcNow);
        return $"{local:ddd MMM d} · {local:h:mm tt} — {relative}";
    }

    private static string FormatRelative(TimeSpan until)
    {
        if (until.TotalSeconds < 60)    return "starting now";
        if (until.TotalMinutes < 60)    return $"in {(int)until.TotalMinutes} min";
        if (until.TotalHours   < 24)    return $"in {(int)Math.Round(until.TotalHours)} hr";
        if (until.TotalDays    < 7)     return $"in {(int)until.TotalDays} days";
        return $"in {(int)(until.TotalDays / 7)} weeks";
    }

    private string DisplayNameFor(string? manifestId)
    {
        if (string.IsNullOrEmpty(manifestId)) return "";
        var g = _settings.Games.FirstOrDefault(x =>
            string.Equals(x.ManifestId, manifestId, StringComparison.OrdinalIgnoreCase));
        return g?.DisplayName ?? manifestId;
    }

    // Reconciles tile state with what's actually on disk. If install-state
    // claims a game is installed but the folder or exe is missing, we
    // silently downgrade the tile to "Not installed" without touching the
    // state file — the next install will rewrite the entry cleanly.
    private void ApplyInstallState(LauncherTile tile, Product product)
    {
        var haveRecord = _state.Products.TryGetValue(tile.ManifestId, out var installed);
        var onDisk = haveRecord
                     && Directory.Exists(installed!.InstallPath)
                     && File.Exists(Path.Combine(installed.InstallPath, installed.ExeName));

        if (haveRecord && onDisk)
        {
            if (string.Equals(installed!.Sha256, product.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                tile.StatusText = "Installed";
                tile.ActionText = "Play";
                tile.Action     = TileAction.Play;
            }
            else
            {
                tile.StatusText = "Update available";
                tile.ActionText = "Update";
                tile.Action     = TileAction.Update;
            }
        }
        else
        {
            tile.StatusText = "Not installed";
            tile.ActionText = "Install";
            tile.Action     = TileAction.Install;
        }

        tile.ActionEnabled      = true;
        tile.ProgressValue      = 0;
        tile.ProgressVisibility = Visibility.Collapsed;
    }

    private void OnTileAction(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.DataContext is not LauncherTile tile) return;

        switch (tile.Action)
        {
            case TileAction.Install:
            case TileAction.Update:
                _ = InstallTileAsync(tile);
                break;
            case TileAction.Play:
                LaunchGame(tile);
                break;
            case TileAction.Cancel:
                tile.CancelSource?.Cancel();
                break;
        }
    }

    private async Task InstallTileAsync(LauncherTile tile)
    {
        if (_manifest == null ||
            !_manifest.Products.TryGetValue(tile.ManifestId, out var product))
        {
            MessageBox.Show(this, "Product not present in current manifest.",
                "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var cts = new CancellationTokenSource();
        tile.CancelSource       = cts;
        tile.Action             = TileAction.Cancel;
        tile.ActionText         = "Cancel";
        tile.StatusText         = "Preparing…";
        tile.ProgressValue      = 0;
        tile.ProgressVisibility = Visibility.Visible;

        var progress = new Progress<InstallProgress>(p =>
        {
            switch (p.Stage)
            {
                case InstallStage.Downloading:
                    var pct = p.BytesTotal > 0 ? (double)p.BytesDone / p.BytesTotal * 100 : 0;
                    tile.StatusText    = $"Downloading… {FormatBytes(p.BytesDone)} / {FormatBytes(p.BytesTotal)}";
                    tile.ProgressValue = pct;
                    break;
                case InstallStage.Verifying:
                    tile.StatusText    = "Verifying…";
                    tile.ProgressValue = 100;
                    break;
                case InstallStage.Extracting:
                    tile.StatusText    = "Extracting…";
                    tile.ProgressValue = 100;
                    break;
                case InstallStage.Finalizing:
                    tile.StatusText    = "Finalizing…";
                    break;
            }
        });

        try
        {
            await _installer.InstallAsync(tile.ManifestId, product, progress, cts.Token);
            ApplyInstallState(tile, product);
        }
        catch (OperationCanceledException)
        {
            ApplyInstallState(tile, product);
            tile.StatusText = "Cancelled";
        }
        catch (Exception ex)
        {
            tile.StatusText         = $"Failed: {ex.Message}";
            tile.ActionText         = "Retry";
            tile.Action             = TileAction.Install;
            tile.ProgressVisibility = Visibility.Collapsed;
            tile.ActionEnabled      = true;
        }
        finally
        {
            tile.CancelSource = null;
        }
    }

    private void LaunchGame(LauncherTile tile)
    {
        if (!_state.Products.TryGetValue(tile.ManifestId, out var installed))
        {
            MessageBox.Show(this, "Install record missing.", "Play failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var exePath = Path.Combine(installed.InstallPath, installed.ExeName);
        if (!File.Exists(exePath))
        {
            MessageBox.Show(this, $"Executable not found:\n{exePath}", "Play failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            // WorkingDirectory is the exe's own directory. Unity games often
            // resolve *_Data/ relative to CWD, and when the zip nests the
            // game inside a subfolder, that subfolder is what CWD needs to
            // be — not the outer install root.
            Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? installed.InstallPath,
                UseShellExecute  = false,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Play failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync();

    protected override void OnClosed(EventArgs e)
    {
        foreach (var tile in _tiles)
            tile.CancelSource?.Cancel();
        base.OnClosed(e);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)                return $"{bytes} B";
        if (bytes < 1024L * 1024)        return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

// ================ Tile view model ================
//
// INotifyPropertyChanged so WPF picks up mid-install status changes.
// Public because reflection-based binding across DataTemplate boundaries
// is more predictable when the source type is public.

public sealed class LauncherTile : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string DisplayName { get; set; } = "";
    public string ManifestId  { get; set; } = "";
    public string SizeText    { get; set; } = "";
    public string UpdatedText { get; set; } = "";

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set { if (_statusText != value) { _statusText = value; Notify(); } }
    }

    private string _actionText = "";
    public string ActionText
    {
        get => _actionText;
        set { if (_actionText != value) { _actionText = value; Notify(); } }
    }

    private bool _actionEnabled = true;
    public bool ActionEnabled
    {
        get => _actionEnabled;
        set { if (_actionEnabled != value) { _actionEnabled = value; Notify(); } }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { if (Math.Abs(_progressValue - value) > 0.001) { _progressValue = value; Notify(); } }
    }

    private Visibility _progressVisibility = Visibility.Collapsed;
    public Visibility ProgressVisibility
    {
        get => _progressVisibility;
        set { if (_progressVisibility != value) { _progressVisibility = value; Notify(); } }
    }

    // Not bound to the UI — internal dispatch state.
    public TileAction              Action       { get; set; }
    public CancellationTokenSource? CancelSource { get; set; }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum TileAction
{
    Install,
    Update,
    Play,
    Cancel,
    Disabled,
}

// View model for one upcoming event in the news panel.
public sealed class EventVm
{
    public string Title    { get; init; } = "";
    public string Body     { get; init; } = "";
    public string When     { get; init; } = "";  // "Fri Jul 10 · 6:11 AM — in 2 hr"
    public string GameName { get; init; } = "";  // display name; may be empty
}