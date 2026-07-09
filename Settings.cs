// Settings.cs
//
// User-editable settings persisted to %APPDATA%\MartianGamesAlerts\settings.json.
//
// GameWatch now carries an optional ManifestId — the id used in the
// launcher manifest (which uses hyphens, e.g. "TankOff-Classic"), separate
// from the observations API's CanonicalName (which uses spaces, e.g.
// "TankOff Classic"). Games with ManifestId=null are simply not shown as
// launcher tiles.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MartianGamesAlerts;

internal sealed class Settings
{
    public string ApiBaseUrl  { get; set; } = "https://martiangames.com";
    public string PortalUrl   { get; set; } = "https://martiangames.com/portal";
    public string ManifestUrl { get; set; } = "https://martiangames.com/pc/manifest.json";

    // Empty means "use the platform default" — see GetInstallRoot().
    public string InstallRoot { get; set; } = "";

    public int PollIntervalSeconds      { get; set; } = 120;
    public int InactiveThresholdMinutes { get; set; } = 10;
    public int RecentThresholdMinutes   { get; set; } = 5;
    public int AlertCooldownMinutes     { get; set; } = 30;

    public List<GameWatch> Games { get; set; } = new();

    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MartianGamesAlerts", "settings.json");

    // Resolves InstallRoot: user-configured value if set, else default of
    // %LOCALAPPDATA%\MartianGames\Games\ .
    public string GetInstallRoot()
    {
        if (!string.IsNullOrWhiteSpace(InstallRoot))
            return InstallRoot;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MartianGames", "Games");
    }

    public static Settings LoadOrCreate()
    {
        var path = GetPath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
                if (loaded != null)
                {
                    // Backfill ManifestId for existing users whose settings
                    // file was written before we added the field.
                    MigrateManifestIds(loaded.Games);
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                try { File.Move(path, path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
                Console.Error.WriteLine($"Failed to load settings: {ex.Message}");
            }
        }

        var fresh = CreateDefault();
        fresh.Save();
        return fresh;
    }

    public void Save()
    {
        var path = GetPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    // Fills in ManifestId on any game where CanonicalName matches a known
    // default. Idempotent: games that already have a value are untouched,
    // and games whose CanonicalName isn't in the default list stay null.
    private static void MigrateManifestIds(List<GameWatch> games)
    {
        var canonical2manifest = CreateDefault().Games
            .Where(g => !string.IsNullOrEmpty(g.CanonicalName) && !string.IsNullOrEmpty(g.ManifestId))
            .ToDictionary(g => g.CanonicalName, g => g.ManifestId!, StringComparer.OrdinalIgnoreCase);

        foreach (var g in games)
        {
            if (!string.IsNullOrEmpty(g.ManifestId)) continue;
            if (string.IsNullOrEmpty(g.CanonicalName)) continue;
            if (canonical2manifest.TryGetValue(g.CanonicalName, out var manifestId))
                g.ManifestId = manifestId;
        }
    }

    private static Settings CreateDefault() => new()
    {
        Games = new List<GameWatch>
        {
            new() { CanonicalName = "MotorWars2",      DisplayName = "Motor Wars 2",     Enabled = true, ManifestId = "MotorWars2" },
            new() { CanonicalName = "AirWars3",        DisplayName = "Air Wars 3",       Enabled = true, ManifestId = "AirWars3" },
            new() { CanonicalName = "AirWars2",        DisplayName = "Air Wars 2",       Enabled = true, ManifestId = "AirWars2" },
            new() { CanonicalName = "TankOff Classic", DisplayName = "Tank Off Classic", Enabled = true, ManifestId = "TankOff-Classic" },
        },
    };
}

internal sealed class GameWatch
{
    public string  CanonicalName { get; set; } = "";
    public string  DisplayName   { get; set; } = "";
    public bool    Enabled       { get; set; } = true;

    // Product ID in the launcher manifest. When set, this game gets a
    // tile in the launcher window. When null, it's tracked only for
    // observation alerts (or not at all).
    public string? ManifestId    { get; set; }

    public DateTime? LastObservedAt { get; set; }
    public DateTime? LastAlertedAt  { get; set; }
}
