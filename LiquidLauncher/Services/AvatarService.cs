using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;

namespace LiquidLauncher.Services;

/// <summary>
/// Fetches Apple-style "Memoji" avatars from the open-source Tapback API
/// (https://github.com/Wimell/Tapback-Memojis, hosted at tapback.co) and
/// caches them on disk so we don't re-download the same face every launch.
///
/// The API is a simple, unauthenticated GET that returns a deterministic
/// .webp for a given seed string:
///   https://www.tapback.co/api/avatar/{seed}.webp
/// The same seed always renders the same avatar, and a random one is
/// available at /api/avatar.webp for a fresh face.
/// </summary>
public static class AvatarService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private static readonly string CacheFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LiquidLauncher", "avatars");

    /// <summary>Returns the cached bitmap for <paramref name="seed"/>, downloading
    /// and caching it first if this is the first time it's been requested.
    /// Returns null on any network/parse failure so callers can fall back to
    /// the letter/emoji avatar instead of crashing.</summary>
    public static async Task<Bitmap?> GetMemojiAsync(string seed)
    {
        if (string.IsNullOrWhiteSpace(seed)) seed = "player";

        try
        {
            Directory.CreateDirectory(CacheFolder);
            var safeName = Uri.EscapeDataString(seed);
            var cachePath = Path.Combine(CacheFolder, $"{safeName}.webp");

            if (!File.Exists(cachePath))
            {
                var url = $"https://www.tapback.co/api/avatar/{safeName}.webp";
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(cachePath, bytes).ConfigureAwait(false);
            }

            await using var stream = File.OpenRead(cachePath);
            return new Bitmap(stream);
        }
        catch
        {
            // Offline, DNS hiccup, service down, etc. — caller falls back gracefully.
            return null;
        }
    }

    /// <summary>Generates a fresh random seed for the "Shuffle" button.</summary>
    public static string NewSeed() => Guid.NewGuid().ToString("N");
}
