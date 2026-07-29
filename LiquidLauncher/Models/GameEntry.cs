using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace LiquidLauncher.Models;

/// <summary>
/// A single game/shortcut the user has added. Persisted to disk as JSON.
/// Implements INotifyPropertyChanged so edits made in the details panel show up
/// immediately on every tile bound to this instance, without rebuilding the list.
/// </summary>
public class GameEntry : INotifyPropertyChanged
{
    private string _name = "";
    private string _colorHex = "#5E5CE6";
    private string? _coverImagePath;
    private string? _backgroundImagePath;
    private string _description = "";
    private string _genre = "";
    private string _notes = "";
    private string _releaseDate = "";
    private string _platform = "PC (Windows)";
    private int _rating;
    private bool _isFavorite;
    private string _status = "Backlog";
    private DateTime? _lastPlayed;
    private Bitmap? _coverBitmap;
    private bool _coverLoadAttempted;
    private Bitmap? _backgroundBitmap;
    private bool _backgroundLoadAttempted;

    public string Name
    {
        get => _name;
        set { _name = value; OnChanged(); OnChanged(nameof(Initial)); }
    }

    public string ExecutablePath { get; set; } = "";

    /// <summary>Hex color used as the tile's fallback background when no cover image is set.</summary>
    public string ColorHex
    {
        get => _colorHex;
        set { _colorHex = value; OnChanged(); OnChanged(nameof(IconBrush)); }
    }

    public string? CoverImagePath
    {
        get => _coverImagePath;
        set
        {
            _coverImagePath = value;
            _coverBitmap = null;
            _coverLoadAttempted = false;
            OnChanged();
            OnChanged(nameof(CoverBitmap));
            OnChanged(nameof(HasCover));
        }
    }

    /// <summary>Wide banner/backdrop image shown behind the details panel and hero card,
    /// distinct from the square cover art used in tiles.</summary>
    public string? BackgroundImagePath
    {
        get => _backgroundImagePath;
        set
        {
            _backgroundImagePath = value;
            _backgroundBitmap = null;
            _backgroundLoadAttempted = false;
            OnChanged();
            OnChanged(nameof(BackgroundBitmap));
            OnChanged(nameof(HasBackground));
        }
    }

    public string Description
    {
        get => _description;
        set { _description = value; OnChanged(); }
    }

    public string ReleaseDate
    {
        get => _releaseDate;
        set { _releaseDate = value; OnChanged(); }
    }

    public string Platform
    {
        get => _platform;
        set { _platform = value; OnChanged(); }
    }

    public string Genre
    {
        get => _genre;
        set { _genre = value; OnChanged(); }
    }

    /// <summary>0-5 star rating the user sets from the detail pane. Persisted like any
    /// other field so it survives a relaunch.</summary>
    public int Rating
    {
        get => _rating;
        set { _rating = value; OnChanged(); }
    }

    public string Notes
    {
        get => _notes;
        set { _notes = value; OnChanged(); }
    }

    /// <summary>Pinned into its own "Favorites" section at the top of the sidebar,
    /// on top of whatever Status section it also belongs to.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { _isFavorite = value; OnChanged(); }
    }

    /// <summary>One of "Playing", "Backlog", "Beaten", "Shelved" — drives which
    /// sidebar section the game is grouped under. Kept as a plain string (rather
    /// than an enum) so older saved libraries without this field just default in.</summary>
    public string Status
    {
        get => _status;
        set { _status = string.IsNullOrWhiteSpace(value) ? "Backlog" : value; OnChanged(); }
    }

    /// <summary>Stamped automatically whenever the game is launched from either
    /// Play button. Null until first launch.</summary>
    public DateTime? LastPlayed
    {
        get => _lastPlayed;
        set { _lastPlayed = value; OnChanged(); OnChanged(nameof(LastPlayedDisplay)); }
    }

    [JsonIgnore]
    public string LastPlayedDisplay => LastPlayed is null ? "Never" : LastPlayed.Value.ToString("MMMM d, yyyy");

    [JsonIgnore]
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    [JsonIgnore]
    public IBrush IconBrush => new SolidColorBrush(Color.Parse(ColorHex));

    [JsonIgnore]
    public bool HasCover => !string.IsNullOrWhiteSpace(CoverImagePath) && File.Exists(CoverImagePath);

    /// <summary>Lazily loaded so we don't decode every cover image up front on startup.</summary>
    [JsonIgnore]
    public Bitmap? CoverBitmap
    {
        get
        {
            if (_coverBitmap is null && !_coverLoadAttempted && HasCover)
            {
                _coverLoadAttempted = true;
                try { _coverBitmap = new Bitmap(CoverImagePath!); }
                catch { _coverBitmap = null; }
            }
            return _coverBitmap;
        }
    }

    [JsonIgnore]
    public bool HasBackground => !string.IsNullOrWhiteSpace(BackgroundImagePath) && File.Exists(BackgroundImagePath);

    /// <summary>Lazily loaded, same pattern as CoverBitmap.</summary>
    [JsonIgnore]
    public Bitmap? BackgroundBitmap
    {
        get
        {
            if (_backgroundBitmap is null && !_backgroundLoadAttempted && HasBackground)
            {
                _backgroundLoadAttempted = true;
                try { _backgroundBitmap = new Bitmap(BackgroundImagePath!); }
                catch { _backgroundBitmap = null; }
            }
            return _backgroundBitmap;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
