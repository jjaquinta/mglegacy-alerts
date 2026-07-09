// SettingsForm.cs
//
// Modal settings dialog. All controls built in code (no .Designer.cs split)
// for a single-file form. Reads from / writes to the shared Settings
// instance on Save; Cancel leaves the instance untouched.

using System.Windows.Forms;

namespace MartianGamesAlerts;

internal sealed class SettingsForm : Form
{
    private readonly Settings _settings;

    private readonly CheckedListBox _gamesList = new();
    private readonly NumericUpDown _pollInterval      = new();
    private readonly NumericUpDown _inactiveThreshold = new();
    private readonly NumericUpDown _recentThreshold   = new();
    private readonly NumericUpDown _cooldown          = new();
    private readonly CheckBox      _runAtStartup      = new();

    public SettingsForm(Settings settings)
    {
        _settings = settings;
        BuildUI();
        LoadFromSettings();
    }

    private void BuildUI()
    {
        Text = "Martian Games Alerts — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 480);
        Font = new Font("Segoe UI", 9F);

        // Try to use the embedded app icon for the form too.
        var iconStream = typeof(SettingsForm).Assembly.GetManifestResourceStream("App.ico");
        if (iconStream != null) Icon = new Icon(iconStream);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(14),
            ColumnCount = 1,
            RowCount = 6,
            AutoSize = true,
        };
        // Rows: games label, games list, timing group, run-at-startup, spacer, buttons
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 130));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // ---- Games to monitor ----
        root.Controls.Add(new Label
        {
            Text = "Games to monitor:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, 0, 0);

        _gamesList.CheckOnClick = true;
        _gamesList.BorderStyle = BorderStyle.FixedSingle;
        _gamesList.Dock = DockStyle.Fill;
        _gamesList.IntegralHeight = false;
        root.Controls.Add(_gamesList, 0, 1);

        // ---- Alert timing group ----
        var group = new GroupBox
        {
            Text = "Alert timing",
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 8, 10, 10),
            Margin = new Padding(0, 12, 0, 8),
        };
        group.Controls.Add(BuildTimingPanel());
        root.Controls.Add(group, 0, 2);

        // ---- Run-at-startup checkbox ----
        _runAtStartup.Text = "Start automatically when I sign in to Windows";
        _runAtStartup.AutoSize = true;
        _runAtStartup.Margin = new Padding(0, 4, 0, 0);
        root.Controls.Add(_runAtStartup, 0, 3);

        // ---- Buttons ----
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        var save   = new Button { Text = "Save",   DialogResult = DialogResult.OK,     Width = 90 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 90 };
        save.Click   += (_, _) => OnSave();
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 5);

        AcceptButton = save;
        CancelButton = cancel;

        Controls.Add(root);
    }

    private TableLayoutPanel BuildTimingPanel()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        ConfigureNumeric(_pollInterval,      minimum: 30,  maximum: 3600);
        ConfigureNumeric(_inactiveThreshold, minimum: 1,   maximum: 240);
        ConfigureNumeric(_recentThreshold,   minimum: 1,   maximum: 120);
        ConfigureNumeric(_cooldown,          minimum: 1,   maximum: 1440);

        AddTimingRow(t, "How often to check for activity:",                 _pollInterval,      "seconds");
        AddTimingRow(t, "No activity for at least this long, then…",        _inactiveThreshold, "minutes");
        AddTimingRow(t, "…activity within the last:",                        _recentThreshold,   "minutes");
        AddTimingRow(t, "Don't notify me again for the same game for:",     _cooldown,          "minutes");

        return t;
    }

    private static void AddTimingRow(TableLayoutPanel t, string label, NumericUpDown nud, string unit)
    {
        var row = t.RowCount;
        t.RowCount = row + 1;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        t.Controls.Add(new Label
        {
            Text = label,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 8, 6),
        }, 0, row);

        nud.Margin = new Padding(0, 3, 4, 3);
        nud.Width = 70;
        t.Controls.Add(nud, 1, row);

        t.Controls.Add(new Label
        {
            Text = unit,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 6),
        }, 2, row);
    }

    private static void ConfigureNumeric(NumericUpDown nud, int minimum, int maximum)
    {
        nud.Minimum = minimum;
        nud.Maximum = maximum;
        nud.Increment = 1;
        nud.ThousandsSeparator = false;
    }

    private void LoadFromSettings()
    {
        _gamesList.Items.Clear();
        foreach (var g in _settings.Games)
            _gamesList.Items.Add(g.DisplayName, g.Enabled);

        _pollInterval.Value      = Clamp(_settings.PollIntervalSeconds,      (int)_pollInterval.Minimum,      (int)_pollInterval.Maximum);
        _inactiveThreshold.Value = Clamp(_settings.InactiveThresholdMinutes, (int)_inactiveThreshold.Minimum, (int)_inactiveThreshold.Maximum);
        _recentThreshold.Value   = Clamp(_settings.RecentThresholdMinutes,   (int)_recentThreshold.Minimum,   (int)_recentThreshold.Maximum);
        _cooldown.Value          = Clamp(_settings.AlertCooldownMinutes,     (int)_cooldown.Minimum,          (int)_cooldown.Maximum);

        _runAtStartup.Checked = StartupRegistration.IsEnabled();
    }

    private void OnSave()
    {
        // Light validation: recent threshold should be ≤ inactive threshold,
        // otherwise the alert can never fire. Warn but don't block — maybe
        // the user knows what they're doing.
        if (_recentThreshold.Value > _inactiveThreshold.Value)
        {
            var result = MessageBox.Show(this,
                "Your 'activity within' value is larger than the 'no activity for' value, " +
                "which means alerts will never fire. Save anyway?",
                "Settings",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning);
            if (result != DialogResult.OK)
            {
                DialogResult = DialogResult.None;
                return;
            }
        }

        for (var i = 0; i < _settings.Games.Count && i < _gamesList.Items.Count; i++)
            _settings.Games[i].Enabled = _gamesList.GetItemChecked(i);

        _settings.PollIntervalSeconds      = (int)_pollInterval.Value;
        _settings.InactiveThresholdMinutes = (int)_inactiveThreshold.Value;
        _settings.RecentThresholdMinutes   = (int)_recentThreshold.Value;
        _settings.AlertCooldownMinutes     = (int)_cooldown.Value;

        _settings.Save();
        StartupRegistration.SetEnabled(_runAtStartup.Checked);
    }

    private static int Clamp(int v, int min, int max) =>
        v < min ? min : (v > max ? max : v);
}
