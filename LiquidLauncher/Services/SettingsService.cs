using System;
using System.IO;
using System.Text.Json;

namespace LiquidLauncher.Services;

public class AppSettings
{
    /// <summary>"Dark" or "Light".</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>Display name shown on the Profile card and used as the source
    /// letter for the "Initial" avatar style.</summary>
    public string ProfileName { get; set; } = "Player";

    /// <summary>"Initial" (colored letter, the default), "Emoji" (a chosen emoji),
    /// or "Memoji" (an Apple-style avatar image from the Tapback service).</summary>
    public string AvatarKind { get; set; } = "Initial";

    /// <summary>Which emoji is shown when AvatarKind is "Emoji".</summary>
    public string AvatarEmoji { get; set; } = "🙂";

    /// <summary>Seed string sent to the Tapback avatar API when AvatarKind is
    /// "Memoji" — the same seed always renders the same face. Regenerated
    /// whenever the user hits "Shuffle" in the avatar picker.</summary>
    public string AvatarMemojiSeed { get; set; } = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Loads/saves app-wide preferences to
/// %AppData%\LiquidLauncher\settings.json so they survive app restarts.
/// </summary>
public static class SettingsService
{
    private static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidLauncher");

    private static readonly string FilePath = Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Corrupt or unreadable file — fall back to defaults rather than crash.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence for this prototype; a real build should surface this.
        }
    }
}
