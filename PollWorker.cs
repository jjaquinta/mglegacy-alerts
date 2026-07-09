// PollWorker.cs
//
// Runs on its own task. On every poll:
//   1. Fetches per-game snapshots (observed_at, player_count, room_nicknames).
//   2. Updates the tooltip with which games are active by name.
//   3. Runs the previous-vs-current transition rule; on hit, calls onAlert
//      with the game display name AND the current player nicknames.
//   4. If this poll was requested via TriggerCheck(showSummary: true),
//      also calls onSummary with the full list of currently-active games
//      so the tray can pop a "who's active right now" toast.

using System.Globalization;
using System.Text.RegularExpressions;

namespace MartianGamesAlerts;

internal sealed record ActiveGame(
    string DisplayName,
    string Nicknames,   // display-formatted; may be empty if server didn't include
    int    PlayerCount);

internal sealed class PollWorker : IDisposable
{
    private readonly Settings _settings;
    private readonly Action<ActiveGame>                 _onAlert;
    private readonly Action<IReadOnlyList<ActiveGame>>  _onSummary;
    private readonly Action<string>                     _onTooltip;
    private readonly ApiClient _api;
    private readonly CancellationTokenSource _cts = new();
    private Task? _task;

    public PollWorker(
        Settings settings,
        Action<ActiveGame> onAlert,
        Action<IReadOnlyList<ActiveGame>> onSummary,
        Action<string> onTooltip)
    {
        _settings   = settings;
        _onAlert    = onAlert;
        _onSummary  = onSummary;
        _onTooltip  = onTooltip;
        _api        = new ApiClient(settings.ApiBaseUrl);
    }

    public void Start() => _task = Task.Run(() => RunLoopAsync(_cts.Token));

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _task?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    // Manual trigger from the tray menu. showSummary=true means the caller
    // wants a "who's active right now" toast in addition to the normal
    // transition alert logic.
    public void TriggerCheck(bool showSummary = false)
        => _ = Task.Run(() => PollOnceAsync(_cts.Token, showSummary));

    public void Dispose() => Stop();

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        catch (TaskCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            await PollOnceAsync(ct, showSummary: false);
            try { await Task.Delay(TimeSpan.FromSeconds(_settings.PollIntervalSeconds), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct, bool showSummary)
    {
        Dictionary<string, GameSnapshot> latestByGame;
        try
        {
            latestByGame = await _api.GetLatestPerGameAsync(ct);
        }
        catch (Exception ex)
        {
            _onTooltip($"Last poll failed: {ex.Message}");
            // On failure, still let the user know check-now happened.
            if (showSummary) _onSummary(Array.Empty<ActiveGame>());
            return;
        }

        var now      = DateTime.UtcNow;
        var inactive = TimeSpan.FromMinutes(_settings.InactiveThresholdMinutes);
        var recent   = TimeSpan.FromMinutes(_settings.RecentThresholdMinutes);
        var cooldown = TimeSpan.FromMinutes(_settings.AlertCooldownMinutes);

        var changed = false;
        var active  = new List<ActiveGame>();

        foreach (var game in _settings.Games)
        {
            if (!game.Enabled) continue;
            if (!latestByGame.TryGetValue(game.CanonicalName, out var snap)) continue;

            var previousObs = game.LastObservedAt;
            game.LastObservedAt = snap.ObservedAt;
            changed = true;

            var currentAge = now - snap.ObservedAt;
            var isRecent   = currentAge < recent;

            var view = new ActiveGame(
                DisplayName: game.DisplayName,
                Nicknames:   FormatNicknames(snap.RoomNicknames),
                PlayerCount: snap.PlayerCount);

            if (isRecent) active.Add(view);

            // Transition detection: previous poll saw a stale record and
            // this poll sees a fresh one → someone just started playing.
            if (previousObs == null) continue;
            var previousAge = now - previousObs.Value;
            if (!(previousAge > inactive && isRecent)) continue;

            // Cooldown suppression so we don't spam.
            if (game.LastAlertedAt is { } last && (now - last) < cooldown) continue;

            game.LastAlertedAt = now;
            _onAlert(view);
        }

        if (changed) _settings.Save();

        var localNow = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var tooltipMsg = active.Count == 0
            ? $"Last poll {localNow} — no games active"
            : $"Last poll {localNow} — {string.Join(", ", active.Select(a => a.DisplayName))} active";
        _onTooltip(tooltipMsg);

        if (showSummary) _onSummary(active);
    }

    // The server emits room_nicknames as "Name1  [lvl],Name2  [lvl],"
    // — double-space before the level bracket and a trailing comma.
    // Normalize to "Name1 [lvl], Name2 [lvl]" for display. Preserves any
    // Unicode as-is (PUN nicknames often contain combining marks).
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private static string FormatNicknames(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(", ", parts.Select(p => WhitespaceRun.Replace(p, " ").Trim()));
    }
}
