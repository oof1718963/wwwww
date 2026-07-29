using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace LiquidLauncher.Services;

/// <summary>One game found on disk by a scan, before the user has chosen to add it.</summary>
public class ScannedGame
{
    public required string Source { get; init; }         // "Steam" or "Epic Games"
    public required string Name { get; init; }
    /// <summary>Shell-executable launch target (a protocol URI, not a raw .exe path —
    /// both Steam and Epic resolve these themselves, updates and all, which is far more
    /// reliable than us guessing which .exe in the install folder is the real one).</summary>
    public required string LaunchUri { get; init; }
}

/// <summary>
/// Finds games already installed via Steam and the Epic Games Launcher, the same way
/// Playnite's built-in importers do: read each platform's own manifest files rather than
/// crawling folders, then hand back a URI (steam://, com.epicgames.launcher://) that lets
/// the platform's client itself resolve and launch the right executable.
/// </summary>
public static class GameScannerService
{
    public static Task<List<ScannedGame>> ScanAsync() => Task.Run(() =>
    {
        var found = new List<ScannedGame>();
        found.AddRange(SafeScan(ScanSteam));
        found.AddRange(SafeScan(ScanEpic));
        return found;
    });

    private static List<ScannedGame> SafeScan(Func<List<ScannedGame>> scan)
    {
        try { return scan(); }
        catch { return new List<ScannedGame>(); }   // Platform not installed / unreadable — skip it, not fatal.
    }

    // ===================== Steam =====================

    private static List<ScannedGame> ScanSteam()
    {
        var results = new List<ScannedGame>();
        var steamPath = FindSteamPath();
        if (steamPath is null) return results;

        foreach (var libraryPath in FindSteamLibraries(steamPath))
        {
            var appsFolder = Path.Combine(libraryPath, "steamapps");
            if (!Directory.Exists(appsFolder)) continue;

            foreach (var manifest in Directory.EnumerateFiles(appsFolder, "appmanifest_*.acf"))
            {
                var text = File.ReadAllText(manifest);
                var appId = VdfValue(text, "appid");
                var name = VdfValue(text, "name");
                var stateFlags = VdfValue(text, "StateFlags");

                // StateFlags "4" == fully installed; skip manifests for games mid-download
                // or only partially there, same filter Playnite applies.
                if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) continue;
                if (stateFlags is not null && stateFlags != "4") continue;

                results.Add(new ScannedGame
                {
                    Source = "Steam",
                    Name = name,
                    LaunchUri = $"steam://rungameid/{appId}"
                });
            }
        }

        return results;
    }

    /// <summary>Steam's own install path from the registry, falling back to the default
    /// Program Files location if the key isn't there for some reason.</summary>
    private static string? FindSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                var path = key?.GetValue("SteamPath") as string;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                    return path.Replace('/', Path.DirectorySeparatorChar);
            }
            catch { /* fall through to default path guess below */ }
        }

        var guess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        return Directory.Exists(guess) ? guess : null;
    }

    /// <summary>Steam installs can span multiple drives — libraryfolders.vdf lists every
    /// one the user has added, in addition to the main Steam folder itself.</summary>
    private static List<string> FindSteamLibraries(string steamPath)
    {
        var libraries = new List<string> { steamPath };

        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return libraries;

        var text = File.ReadAllText(vdfPath);
        foreach (Match match in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\""))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(path) && !libraries.Contains(path, StringComparer.OrdinalIgnoreCase))
                libraries.Add(path);
        }

        return libraries;
    }

    /// <summary>Steam's .acf/.vdf format is "key" "value" pairs, one per line — good
    /// enough to pull out with a regex instead of pulling in a full VDF parser dependency.</summary>
    private static string? VdfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }

    // ===================== Epic Games Launcher =====================

    private static List<ScannedGame> ScanEpic()
    {
        var results = new List<ScannedGame>();

        var manifestsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestsFolder)) return results;

        foreach (var manifestFile in Directory.EnumerateFiles(manifestsFolder, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestFile));
                var root = doc.RootElement;

                var name = root.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var appName = root.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                var installLocation = root.TryGetProperty("InstallLocation", out var l) ? l.GetString() : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(appName)) continue;
                // A manifest lingers after uninstall until the launcher gets around to
                // cleaning it up — skip anything whose install folder no longer exists.
                if (!string.IsNullOrWhiteSpace(installLocation) && !Directory.Exists(installLocation)) continue;

                results.Add(new ScannedGame
                {
                    Source = "Epic Games",
                    Name = name!,
                    LaunchUri = $"com.epicgames.launcher://apps/{appName}?action=launch&silent=true"
                });
            }
            catch { /* one bad manifest shouldn't sink the rest of the scan */ }
        }

        return results;
    }
}
