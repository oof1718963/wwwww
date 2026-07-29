using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using LiquidLauncher.Models;

namespace LiquidLauncher.Services;

/// <summary>
/// Loads/saves the user's added games to
/// %AppData%\LiquidLauncher\games.json so the library survives app restarts.
/// </summary>
public static class GameLibrary
{
    private static readonly string FolderPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LiquidLauncher");

    private static readonly string FilePath = Path.Combine(FolderPath, "games.json");

    public static List<GameEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<GameEntry>();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<GameEntry>>(json) ?? new List<GameEntry>();
        }
        catch
        {
            // Corrupt or unreadable file — start fresh rather than crash the app.
            return new List<GameEntry>();
        }
    }

    public static void Save(IEnumerable<GameEntry> games)
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            var json = JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Best-effort persistence for this prototype; a real build should surface this.
        }
    }
}
