// TrayAppContext.cs
//
// Holds the NotifyIcon and bridges the polling worker (background thread)
// to the WinForms UI thread for alert display. Also manages a singleton
// LauncherWindow (WPF) that opens from the tray.

using System.Diagnostics;
using System.Windows.Forms;

namespace MartianGamesAlerts;

internal sealed class TrayAppContext : ApplicationContext
{
    private const int MaxBalloonTextLen = 250;

    private readonly Settings _settings;
    private readonly NotifyIcon _icon;
    private readonly PollWorker _worker;
    private readonly SynchronizationContext _uiSync;
    private LauncherWindow? _launcherWindow;

    // Tracks the intent of the most recent balloon so a click can be
    // routed to the right action. Reset to None after each click.
    private enum BalloonKind { None, Update, Info }
    private BalloonKind _lastBalloonKind = BalloonKind.None;

    public TrayAppContext(Settings settings)
    {
        UpdateLog.Line("STARTUP", "TrayAppContext ctor: enter");
        _settings = settings;
        _uiSync = SynchronizationContext.Current ?? new SynchronizationContext();

        Icon icon;
        try
        {
            icon = LoadAppIcon();
            UpdateLog.Line("STARTUP", "TrayAppContext ctor: icon loaded");
        }
        catch (Exception ex)
        {
            UpdateLog.Line("STARTUP", $"TrayAppContext ctor: icon load threw ({ex.GetType().Name}: {ex.Message}); using SystemIcons.Information");
            icon = SystemIcons.Information;
        }

        _icon = new NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "Martian Games Alerts",
            ContextMenuStrip = BuildMenu(),
        };
        UpdateLog.Line("STARTUP", "TrayAppContext ctor: NotifyIcon constructed and visible");

        _icon.BalloonTipClicked += OnBalloonTipClicked;
        _icon.DoubleClick      += (_, _) => ShowLauncher();

        _worker = new PollWorker(
            _settings,
            onAlert:   OnAlertFromWorker,
            onSummary: OnSummaryFromWorker,
            onTooltip: OnTooltipFromWorker);
        _worker.Start();
        UpdateLog.Line("STARTUP", "TrayAppContext ctor: PollWorker started");

        // Fire-and-forget: log manifest contents AND check whether we
        // should prompt the user to self-update.
        _ = Task.Run(async () =>
        {
            await UpdateChecker.CheckAsync(_settings.ManifestUrl);
            var status = await SelfUpdater.CheckForUpdateAsync(_settings.ManifestUrl);
            if (status == UpdateStatus.UpdateAvailable)
            {
                _uiSync.Post(_ => ShowUpdateAvailableBalloon(), null);
            }
        });
        UpdateLog.Line("STARTUP", "TrayAppContext ctor: exit");
    }

    private static Icon LoadAppIcon()
    {
        var stream = typeof(TrayAppContext).Assembly.GetManifestResourceStream("App.ico");
        return stream != null ? new Icon(stream) : SystemIcons.Information;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show launcher…",        null, (_, _) => ShowLauncher());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Check now",             null, (_, _) => _worker.TriggerCheck(showSummary: true));
        menu.Items.Add("Check for updates",     null, (_, _) => CheckForUpdatesAndPrompt());
        menu.Items.Add("Settings…",             null, (_, _) => ShowSettings());
        menu.Items.Add("Open portal",           null, (_, _) => OpenPortal());
        menu.Items.Add("Open settings folder",  null, (_, _) => OpenSettingsFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit",                  null, (_, _) => ExitThread());
        return menu;
    }

    private void ShowLauncher()
    {
        // Singleton: if a window is already open, bring it forward instead
        // of spawning another. The Closed handler nulls the field so the
        // next click after a user-closed window creates a fresh instance.
        if (_launcherWindow != null)
        {
            _launcherWindow.Activate();
            return;
        }

        _launcherWindow = new LauncherWindow(_settings);
        _launcherWindow.Closed += (_, _) => _launcherWindow = null;
        _launcherWindow.Show();
    }

    // ---- Self-update UI ----

    private void CheckForUpdatesAndPrompt()
    {
        _ = Task.Run(async () =>
        {
            var status = await SelfUpdater.CheckForUpdateAsync(_settings.ManifestUrl);
            _uiSync.Post(_ =>
            {
                switch (status)
                {
                    case UpdateStatus.UpdateAvailable:
                        ShowUpdateAvailableBalloon();
                        break;
                    case UpdateStatus.UpToDate:
                        ShowInfoBalloon("Up to date",
                            "You have the latest version.");
                        break;
                    case UpdateStatus.NotApplicable:
                        ShowInfoBalloon("Development build",
                            "Self-update is disabled for local builds.");
                        break;
                    case UpdateStatus.CannotDetermine:
                        ShowInfoBalloon("Update check failed",
                            "Couldn't check for updates. Try again later.");
                        break;
                }
            }, null);
        });
    }

    private void ShowUpdateAvailableBalloon()
    {
        _lastBalloonKind = BalloonKind.Update;
        _icon.BalloonTipTitle = "Update available";
        _icon.BalloonTipText  = "A new version is ready. Click here to install.";
        _icon.BalloonTipIcon  = ToolTipIcon.Info;
        _icon.ShowBalloonTip(15_000);
    }

    private void ShowInfoBalloon(string title, string text)
    {
        _lastBalloonKind = BalloonKind.Info;
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText  = text;
        _icon.BalloonTipIcon  = ToolTipIcon.Info;
        _icon.ShowBalloonTip(5_000);
    }

    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        var kind = _lastBalloonKind;
        _lastBalloonKind = BalloonKind.None;
        switch (kind)
        {
            case BalloonKind.Update:
                _ = TriggerSelfUpdateAsync();
                break;
            default:
                // Player-activity balloon or an Info balloon — legacy
                // behavior was to open the portal.
                OpenPortal();
                break;
        }
    }

    private async Task TriggerSelfUpdateAsync()
    {
        try
        {
            _icon.Text = "Installing update…";
            await SelfUpdater.DoInPlaceUpdateAsync(_settings.ManifestUrl);
            // Success — exit so the newly spawned instance takes over.
            ExitThread();
        }
        catch (Exception ex)
        {
            _icon.Text = "Martian Games Alerts";
            MessageBox.Show(
                $"Update failed:\n\n{ex.Message}",
                "Martian Games Alerts",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void OnAlertFromWorker(ActiveGame game)
    {
        _uiSync.Post(_ =>
        {
            _icon.BalloonTipTitle = "Players online";
            _icon.BalloonTipText  = Truncate(
                string.IsNullOrEmpty(game.Nicknames)
                    ? $"Someone just started playing {game.DisplayName}."
                    : $"Someone just started playing {game.DisplayName}: {game.Nicknames}",
                MaxBalloonTextLen);
            _icon.BalloonTipIcon  = ToolTipIcon.Info;
            _icon.ShowBalloonTip(10_000);
        }, null);
    }

    private void OnSummaryFromWorker(IReadOnlyList<ActiveGame> active)
    {
        _uiSync.Post(_ =>
        {
            _icon.BalloonTipTitle = "Currently playing";
            _icon.BalloonTipText  = active.Count == 0
                ? "No one is currently playing."
                : Truncate(
                    string.Join("\n",
                        active.Select(a => string.IsNullOrEmpty(a.Nicknames)
                            ? a.DisplayName
                            : $"{a.DisplayName}: {a.Nicknames}")),
                    MaxBalloonTextLen);
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(10_000);
        }, null);
    }

    private void OnTooltipFromWorker(string tooltip)
    {
        var truncated = tooltip.Length > 63 ? tooltip.Substring(0, 60) + "..." : tooltip;
        _uiSync.Post(_ => _icon.Text = truncated, null);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s.Substring(0, max - 3) + "...";

    private void ShowSettings()
    {
        using var form = new SettingsForm(_settings);
        form.ShowDialog();
    }

    private void OpenPortal()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _settings.PortalUrl,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            var folder = Path.GetDirectoryName(Settings.GetPath())!;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch { }
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _worker.Stop();
        _launcherWindow?.Close();
        _icon.Dispose();
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _worker.Stop();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}