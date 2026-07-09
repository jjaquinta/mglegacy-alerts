// InstallState.cs
//
// Persistent record of what's installed where. Written to
// %APPDATA%\MartianGamesAlerts\install-state.json next to settings.json.
//
// The launcher uses this to decide per-tile state: Not installed / Update
// available / Installed. On mismatch with the filesystem (folder deleted
// externally), the launcher silently downgrades to "Not installed" without
// mutating the file — that gets rewritten cleanly on the next install.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace MartianGamesAlerts;

internal sealed class InstallState
{
    public Dictionary<string, InstalledProduct> Products { get; set; } = new();

    [JsonIgnore]
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string GetPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MartianGamesAlerts", "install-state.json");

    public static InstallState LoadOrCreate()
    {
        var path = GetPath();
        if (File.Exists(path))
        {
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize<InstallState>(json, JsonOpts);
                if (loaded != null) return loaded;
            }
            catch (Exception ex)
            {
                try { File.Move(path, path + ".broken-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss")); } catch { }
                Console.Error.WriteLine($"Failed to load install-state: {ex.Message}");
            }
        }
        return new InstallState();
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
}

internal sealed class InstalledProduct
{
    public string   ManifestId  { get; set; } = "";
    public string   InstallPath { get; set; } = "";
    public string   Sha256      { get; set; } = "";  // matches manifest at install time
    public string   ExeName     { get; set; } = "";  // relative to InstallPath
    public long     SizeBytes   { get; set; }
    public DateTime InstalledAt { get; set; }
}
