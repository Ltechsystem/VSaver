using System.Text.Json;
using System.Text.Json.Serialization;
using ValheimSync.Core.Sync;

namespace ValheimSync.Core;

/// <summary>
/// Persisted next to the executable as settings.json.
/// Kept deliberately simple so non-technical friends can be sent one folder.
/// </summary>
public sealed class AppSettings
{
    /// <summary>
    /// The player's identity, used for world locks. Defaults to empty; on first connect
    /// it's auto-filled from the signed-in Google account's email (local part, before the @).
    /// The user can override it with the pen icon in the UI.
    /// </summary>
    public string PlayerName { get; set; } = "";

    /// <summary>
    /// The shared Google Drive folder everyone in the group syncs against. Persisted to
    /// settings.json so a deployment can point at a different folder without rebuilding;
    /// when absent it falls back to <see cref="DefaultDriveFolderId"/>.
    /// </summary>
    public string DriveFolderId { get; set; } = DefaultDriveFolderId;

    /// <summary>The group's shared folder, used when settings.json doesn't specify one.</summary>
    public const string DefaultDriveFolderId = "1dgMEB7cihhT78u1xdyOQjARhsZukyERb";

    /// <summary>
    /// The folder id to actually connect to: the configured one, or the default if it was
    /// left blank in settings.json.
    /// </summary>
    [JsonIgnore]
    public string EffectiveDriveFolderId =>
        string.IsNullOrWhiteSpace(DriveFolderId) ? DefaultDriveFolderId : DriveFolderId.Trim();

    /// <summary>Override if Valheim saves live somewhere non-standard.</summary>
    public string? WorldsPathOverride { get; set; }

    /// <summary>Fallback poll interval for remote changes.</summary>
    public int PollIntervalMinutes { get; set; } = 5;

    /// <summary>Seconds a save file must be untouched before we trust it is complete.</summary>
    public int DebounceSeconds { get; set; } = 60;

    /// <summary>
    /// While Valheim is open, how often (minutes) to push the in-progress save if it
    /// changed and has settled. Keeps a crash from losing more than this interval and
    /// lets friends see progress without waiting for the game to close.
    /// </summary>
    public int InGameUploadMinutes { get; set; } = 5;

    /// <summary>World names the user has ticked for syncing.</summary>
    public HashSet<string> SelectedWorlds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Where this machine's Valheim keeps its worlds. Uses the explicit override
    /// if set, otherwise auto-detects (Steam-Cloud location first, then LocalLow).
    /// Not persisted — it's resolved fresh per machine.
    /// </summary>
    [JsonIgnore]
    public string WorldsPath =>
        !string.IsNullOrWhiteSpace(WorldsPathOverride)
            ? WorldsPathOverride
            : ValheimSaveLocations.ResolveWorldsFolder();

    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new();
        }
        catch { /* corrupt settings — start fresh rather than crash */ }
        return new AppSettings();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }
}
