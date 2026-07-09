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

    public TrayAppContext(Settings settings)
    {
        _settings = settings;
        _uiSync = SynchronizationContext.Current ?? new SynchronizationContext();

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Visible = true,
            Text = "Martian Games Alerts",
            ContextMenuStrip = BuildMenu(),
        };

        _icon.BalloonTipClicked += (_, _) => OpenPortal();
        _icon.DoubleClick      += (_, _) => ShowLauncher();

        _worker = new PollWorker(
            _settings,
            onAlert:   OnAlertFromWorker,
            onSummary: OnSummaryFromWorker,
            onTooltip: OnTooltipFromWorker);
        _worker.Start();

        _ = Task.Run(() => UpdateChecker.CheckAsync(_settings.ManifestUrl));
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
        menu.Items.Add("Check for updates",     null,
            (_, _) => _ = Task.Run(() => UpdateChecker.CheckAsync(_settings.ManifestUrl)));
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