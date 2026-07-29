using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LiquidLauncher.Models;
using LiquidLauncher.Services;

namespace LiquidLauncher;

public partial class MainWindow : Window
{
    // Cycled through for each newly added game so tiles/fallback icons aren't all the same color.
    private static readonly string[] Palette =
    {
        "#5E5CE6", "#FF6482", "#FF9F0A", "#34C759", "#0A84FF", "#BF5AF2"
    };

    private static readonly IBrush StarFilledBrush = new SolidColorBrush(Color.Parse("#FF9F0A"));
    private static readonly IBrush StarEmptyBrush = new SolidColorBrush(Color.Parse("#4D8E8E93"));

    private enum SidebarGroupMode { Status, Platform, Rating, None }

    // Fixed display order for the Status grouping; anything else (custom/legacy
    // status strings) falls back to alphabetical after these.
    private static readonly string[] StatusOrder = { "Playing", "Backlog", "Shelved", "Beaten" };
    private static readonly string[] RatingOrder = { "5 Stars", "4 Stars", "3 Stars", "2 Stars", "1 Star", "Unrated" };

    private readonly ObservableCollection<GameEntry> _games = new();
    private readonly ObservableCollection<GameEntry> _displayGames = new();

    /// <summary>What SidebarList actually binds to — a mix of SidebarHeader section
    /// labels and GameEntry rows, rebuilt whenever the filter or group mode changes.</summary>
    private readonly ObservableCollection<object> _sidebarItems = new();

    private SidebarGroupMode _groupMode = SidebarGroupMode.Status;
    private bool _sidebarCollapsed;
    private int _nextColorIndex;
    private GameEntry? _editingEntry;
    private string? _pendingCoverPath;
    private string? _pendingBackgroundPath;

    /// <summary>The game currently shown in the right-hand detail pane (driven by the
    /// sidebar selection).</summary>
    private GameEntry? _selectedEntry;

    public MainWindow()
    {
        InitializeComponent();

        foreach (var game in GameLibrary.Load())
            _games.Add(game);

        SidebarList.ItemsSource = _sidebarItems;

        // Header rows ("Backlog (1)", "Playing (2)", ...) are plain labels, not real
        // rows — but by default they still get the same ListBoxItem padding/hover/
        // selection chrome as game rows, which is what made them look like an odd,
        // off-position highlighted pill instead of a plain section label.
        SidebarList.ContainerPrepared += (_, args) =>
        {
            if (args.Container is not ListBoxItem item) return;

            if (item.DataContext is SidebarHeader)
            {
                item.Padding = new Thickness(0);
                item.Focusable = false;
                item.IsHitTestVisible = false;
            }
            else
            {
                // The list virtualizes and recycles containers, so a container that was
                // previously configured as a header (see above) can get reused for a
                // real game row later — without this reset, that row would silently
                // stay unclickable. Clear back to the theme's own defaults instead of
                // guessing the original values.
                item.ClearValue(ContentControl.PaddingProperty);
                item.ClearValue(InputElement.FocusableProperty);
                item.ClearValue(InputElement.IsHitTestVisibleProperty);
            }
        };

        _games.CollectionChanged += (_, _) =>
        {
            ApplyFilter();
            EnsureSelection();
        };

        ApplyFilter();
        EnsureSelection();

        ApplyAvatarVisual();
    }

    /// <summary>Rebuilds the filtered sidebar list from the search box text and keeps
    /// the empty-state / detail-pane visibility in sync with whether the library has
    /// anything in it at all. Also rebuilds the grouped/sectioned display collection
    /// that the sidebar ListBox actually binds to.</summary>
    private void ApplyFilter()
    {
        var query = SearchBox?.Text?.Trim();

        _displayGames.Clear();
        foreach (var game in _games)
        {
            if (string.IsNullOrWhiteSpace(query) ||
                game.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _displayGames.Add(game);
            }
        }

        SidebarEmptyText.IsVisible = _displayGames.Count == 0;

        var hasAnyGames = _games.Count > 0;
        EmptyStateView.IsVisible = !hasAnyGames;
        DetailPaneContent.IsVisible = hasAnyGames;

        RebuildSidebarSections();
    }

    /// <summary>Sections the currently-filtered games into a flat list of
    /// SidebarHeader + GameEntry rows for the sidebar ListBox — Favorites always
    /// pinned first (native macOS convention), then a Status, Platform, or no
    /// grouping at all depending on _groupMode.</summary>
    private void RebuildSidebarSections()
    {
        _sidebarItems.Clear();

        var favorites = _displayGames.Where(g => g.IsFavorite).OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (favorites.Count > 0)
        {
            _sidebarItems.Add(new SidebarHeader { Title = $"Favorites ({favorites.Count})" });
            foreach (var game in favorites) _sidebarItems.Add(game);
        }

        switch (_groupMode)
        {
            case SidebarGroupMode.Status:
                AddGroupedSections(g => string.IsNullOrWhiteSpace(g.Status) ? "Backlog" : g.Status, StatusOrder);
                break;
            case SidebarGroupMode.Platform:
                AddGroupedSections(g => string.IsNullOrWhiteSpace(g.Platform) ? "Other" : g.Platform, Array.Empty<string>());
                break;
            case SidebarGroupMode.Rating:
                AddGroupedSections(g => g.Rating switch
                {
                    5 => "5 Stars",
                    4 => "4 Stars",
                    3 => "3 Stars",
                    2 => "2 Stars",
                    1 => "1 Star",
                    _ => "Unrated"
                }, RatingOrder);
                break;
            case SidebarGroupMode.None:
                _sidebarItems.Add(new SidebarHeader { Title = $"All Games ({_displayGames.Count})" });
                foreach (var game in _displayGames.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                    _sidebarItems.Add(game);
                break;
        }

        // Clearing/re-adding the collection drops the ListBox's selection even when
        // the same GameEntry reference comes back, so restore it explicitly.
        SidebarList.SelectedItem = _selectedEntry is not null && _displayGames.Contains(_selectedEntry)
            ? _selectedEntry
            : null;
    }

    /// <summary>Groups every currently-filtered game by <paramref name="keySelector"/>
    /// into its own "Header (n)" section, in <paramref name="priorityOrder"/> first,
    /// then any remaining keys alphabetically — so custom/legacy values never get
    /// silently dropped from the sidebar.</summary>
    private void AddGroupedSections(Func<GameEntry, string> keySelector, string[] priorityOrder)
    {
        var groups = _displayGames.GroupBy(keySelector).ToDictionary(g => g.Key, g => g.ToList());

        var orderedKeys = priorityOrder.Where(groups.ContainsKey)
            .Concat(groups.Keys.Except(priorityOrder).OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        foreach (var key in orderedKeys)
        {
            var games = groups[key];
            _sidebarItems.Add(new SidebarHeader { Title = $"{key} ({games.Count})" });
            foreach (var game in games.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
                _sidebarItems.Add(game);
        }
    }

    /// <summary>Keeps the sidebar selection (and therefore the detail pane) pointed at
    /// a real game whenever the library or the search filter changes — picking the
    /// first visible game if the previous selection disappeared.</summary>
    private void EnsureSelection()
    {
        if (_games.Count == 0)
        {
            _selectedEntry = null;
            return;
        }

        if (_selectedEntry is null || !_games.Contains(_selectedEntry))
        {
            SidebarList.SelectedItem = _displayGames.FirstOrDefault();
        }
        else
        {
            SidebarList.SelectedItem = _displayGames.Contains(_selectedEntry) ? _selectedEntry : null;
        }
    }

    private void SidebarList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SidebarList.SelectedItem is GameEntry entry)
        {
            _selectedEntry = entry;
            ShowSelectedGame(entry);
        }
        else if (SidebarList.SelectedItem is SidebarHeader)
        {
            // Not a real row — snap the visible selection back to whatever game
            // was actually selected rather than leaving a header highlighted
            // with a stale detail pane behind it.
            SidebarList.SelectedItem = _selectedEntry;
        }
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ApplyFilter();
        EnsureSelection();
    }

    private void SidebarToggle_Click(object? sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        SidebarPane.Width = _sidebarCollapsed ? 0 : 240;
    }

    private void GroupByStatus_Click(object? sender, RoutedEventArgs e)
    {
        _groupMode = SidebarGroupMode.Status;
        RebuildSidebarSections();
    }

    private void GroupByPlatform_Click(object? sender, RoutedEventArgs e)
    {
        _groupMode = SidebarGroupMode.Platform;
        RebuildSidebarSections();
    }

    private void GroupByRating_Click(object? sender, RoutedEventArgs e)
    {
        _groupMode = SidebarGroupMode.Rating;
        RebuildSidebarSections();
    }

    private void GroupByNone_Click(object? sender, RoutedEventArgs e)
    {
        _groupMode = SidebarGroupMode.None;
        RebuildSidebarSections();
    }

    /// <summary>Populates the detail pane (hero art, title, description, rating, and
    /// the info row) for whichever game is currently selected in the sidebar.</summary>
    private void ShowSelectedGame(GameEntry entry)
    {
        DetailHeaderTitle.Text = entry.Name;
        DetailHeroTitle.Text = entry.Name;
        DetailHeroEyebrow.Text = string.IsNullOrWhiteSpace(entry.Genre)
            ? entry.Status?.ToUpperInvariant() ?? "GAME"
            : entry.Genre.ToUpperInvariant();

        var hasArt = entry.HasBackground || entry.HasCover;
        DetailHeroImage.Source = entry.HasBackground ? entry.BackgroundBitmap : entry.CoverBitmap;
        DetailHeroFallback.IsVisible = !hasArt;

        DetailDescriptionText.Text = string.IsNullOrWhiteSpace(entry.Description)
            ? "No description yet. Click the pencil icon to add one."
            : entry.Description;

        DetailReleaseDateText.Text = string.IsNullOrWhiteSpace(entry.ReleaseDate) ? "—" : entry.ReleaseDate;
        DetailPlatformText.Text = string.IsNullOrWhiteSpace(entry.Platform) ? "—" : entry.Platform;
        DetailGenreText.Text = string.IsNullOrWhiteSpace(entry.Genre) ? "—" : entry.Genre;
        DetailLastPlayedText.Text = entry.LastPlayedDisplay;
        DetailStatusText.Text = entry.Status;

        UpdateStars(entry.Rating);
    }

    private void UpdateStars(int rating)
    {
        var starControls = new[] { Star1, Star2, Star3, Star4, Star5 };
        for (var i = 0; i < starControls.Length; i++)
            starControls[i].Fill = i < rating ? StarFilledBrush : StarEmptyBrush;
    }

    /// <summary>Clicking a star sets the rating; clicking the star that's already the
    /// top of the current rating clears it back to zero, same as most star pickers.</summary>
    private void Star_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_selectedEntry is null) return;
        if (sender is not Control { Tag: string tagText } || !int.TryParse(tagText, out var value)) return;

        _selectedEntry.Rating = _selectedEntry.Rating == value ? 0 : value;
        UpdateStars(_selectedEntry.Rating);
        GameLibrary.Save(_games);
    }

    private void PlaySelected_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;
        LaunchGame(_selectedEntry);
    }

    /// <summary>Play button inside the details/edit overlay — launches whichever
    /// game is currently being edited rather than whatever's selected in the
    /// sidebar behind it (they're usually the same, but not always: opening
    /// "Edit" on one game doesn't change the sidebar selection).</summary>
    private void PlayFromDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingEntry is null) return;
        LaunchGame(_editingEntry);
    }

    private void LaunchGame(GameEntry entry)
    {
        try
        {
            var process = Process.Start(new ProcessStartInfo(entry.ExecutablePath) { UseShellExecute = true });
            entry.LastPlayed = DateTime.Now;

            var wasBacklog = entry.Status == "Backlog";
            if (wasBacklog) entry.Status = "Playing";
            GameLibrary.Save(_games);
            if (ReferenceEquals(entry, _selectedEntry)) ShowSelectedGame(entry);

            // Flip the status back once the game actually exits, so "Playing" reflects
            // whether it's currently running rather than sticking forever. Only do this
            // for launches that auto-flipped Backlog->Playing above — if the user picked
            // "Playing" themselves (or any other status) manually, leave it alone.
            if (wasBacklog && process is not null)
            {
                try
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
                    {
                        if (entry.Status == "Playing")
                        {
                            entry.Status = "Backlog";
                            GameLibrary.Save(_games);
                            if (ReferenceEquals(entry, _selectedEntry)) ShowSelectedGame(entry);
                            RebuildSidebarSections();
                        }
                    });
                }
                catch (Exception ex)
                {
                    // UseShellExecute launches (e.g. via a shortcut/URL) don't always
                    // support Exited notifications on every platform — that's fine,
                    // the status just won't auto-revert for those.
                    Debug.WriteLine($"Couldn't track exit for {entry.Name}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch {entry.Name}: {ex.Message}");
        }
    }

    private void EditSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry is null) return;
        OpenDetails(_selectedEntry);
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            e.Handled = true; // stop this same press from bubbling up and re-triggering
                               // BeginMoveDrag on a parent element that has the same handler
        }
    }

    private void Close_Click(object? sender, RoutedEventArgs e) => Close();

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private bool _suppressAvatarSave;

    private void OpenProfile_Click(object? sender, RoutedEventArgs e)
    {
        // Always reopen in view mode, showing whatever's currently saved.
        ProfileViewSection.IsVisible = true;
        ProfileEditSection.IsVisible = false;
        ApplyAvatarVisual();
        ProfileOverlay.IsVisible = true;
    }

    private void CloseProfile_Click(object? sender, RoutedEventArgs e) => ProfileOverlay.IsVisible = false;
    private void ProfileBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => ProfileOverlay.IsVisible = false;
    private void ProfilePanel_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    /// <summary>Opens the inline editor (pencil badge on the avatar, or the "Edit
    /// Profile" button in view mode — both lead here) and seeds its controls from
    /// whatever's currently saved, without re-triggering a save on the way in.</summary>
    private void EditAvatarBadge_Click(object? sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();

        _suppressAvatarSave = true;
        ProfileNameBox.Text = settings.ProfileName;

        var isEmoji = settings.AvatarKind == "Emoji";
        var isMemoji = settings.AvatarKind == "Memoji";
        AvatarKindEmojiOption.IsChecked = isEmoji;
        AvatarKindMemojiOption.IsChecked = isMemoji;
        AvatarKindInitialOption.IsChecked = !isEmoji && !isMemoji;
        PositionAvatarKindIndicator(isEmoji ? AvatarKindEmojiOption : isMemoji ? AvatarKindMemojiOption : AvatarKindInitialOption);
        EmojiPickerPanel.IsVisible = isEmoji;
        MemojiPickerPanel.IsVisible = isMemoji;
        HighlightSelectedEmoji(settings.AvatarEmoji);
        _ = LoadMemojiPreviewAsync(settings.AvatarMemojiSeed);
        _suppressAvatarSave = false;

        ProfileViewSection.IsVisible = false;
        ProfileEditSection.IsVisible = true;
    }

    private void DoneEditingProfile_Click(object? sender, RoutedEventArgs e)
    {
        ProfileEditSection.IsVisible = false;
        ProfileViewSection.IsVisible = true;
        ApplyAvatarVisual();
    }

    private void ProfileNameBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressAvatarSave) return;

        var name = string.IsNullOrWhiteSpace(ProfileNameBox.Text) ? "Player" : ProfileNameBox.Text.Trim();
        var settings = SettingsService.Load();
        settings.ProfileName = name;
        SettingsService.Save(settings);

        ApplyAvatarVisual();
    }

    private void AvatarKindOption_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control target) return;
        PositionAvatarKindIndicator(target);

        var isEmoji = target == AvatarKindEmojiOption;
        var isMemoji = target == AvatarKindMemojiOption;
        EmojiPickerPanel.IsVisible = isEmoji;
        MemojiPickerPanel.IsVisible = isMemoji;

        if (_suppressAvatarSave) return;

        var settings = SettingsService.Load();
        settings.AvatarKind = isEmoji ? "Emoji" : isMemoji ? "Memoji" : "Initial";
        SettingsService.Save(settings);

        if (isMemoji) _ = LoadMemojiPreviewAsync(settings.AvatarMemojiSeed);
        ApplyAvatarVisual();
    }

    /// <summary>Rerolls the Memoji seed to a new random face, saves it, and
    /// refreshes both the in-picker preview and the applied avatar.</summary>
    private void ShuffleMemoji_Click(object? sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Load();
        settings.AvatarMemojiSeed = AvatarService.NewSeed();
        settings.AvatarKind = "Memoji";
        SettingsService.Save(settings);

        _ = LoadMemojiPreviewAsync(settings.AvatarMemojiSeed);
        ApplyAvatarVisual();
    }

    /// <summary>Fetches (or loads from cache) the Tapback avatar for the given
    /// seed and drops it into the small preview swatch in the picker.</summary>
    private async Task LoadMemojiPreviewAsync(string seed)
    {
        var bitmap = await AvatarService.GetMemojiAsync(seed);
        if (bitmap is not null) MemojiPickerPreview.Source = bitmap;
    }

    private void EmojiOption_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Content: string emoji }) return;

        var settings = SettingsService.Load();
        settings.AvatarEmoji = emoji;
        // Picking a face implies you want it shown, even if "Letter" was still selected.
        settings.AvatarKind = "Emoji";
        SettingsService.Save(settings);

        _suppressAvatarSave = true;
        AvatarKindEmojiOption.IsChecked = true;
        PositionAvatarKindIndicator(AvatarKindEmojiOption);
        _suppressAvatarSave = false;

        HighlightSelectedEmoji(emoji);
        ApplyAvatarVisual();
    }

    private void HighlightSelectedEmoji(string emoji)
    {
        foreach (var child in EmojiPickerPanel.Children)
        {
            if (child is Button { Content: string content } button)
                button.Classes.Set("selected", content == emoji);
        }
    }

    /// <summary>Slides the glass highlight behind whichever avatar-kind option is
    /// active (same mechanism as the Dark/Light theme pill).</summary>
    private void PositionAvatarKindIndicator(Control target)
    {
        if (AvatarKindIndicator is null) return;
        var x = target == AvatarKindEmojiOption ? 73 : target == AvatarKindMemojiOption ? 147 : 0;
        AvatarKindIndicator.RenderTransform = new TranslateTransform(x, 0);
    }

    /// <summary>Pushes the saved name/avatar into every place it's shown: the top-bar
    /// avatar button, and the Profile card's name + avatar preview.</summary>
    private void ApplyAvatarVisual()
    {
        var settings = SettingsService.Load();
        var name = string.IsNullOrWhiteSpace(settings.ProfileName) ? "Player" : settings.ProfileName;
        var initial = char.ToUpperInvariant(name.TrimStart()[0]).ToString();
        var isEmoji = settings.AvatarKind == "Emoji";
        var isMemoji = settings.AvatarKind == "Memoji";
        var isInitial = !isEmoji && !isMemoji;

        ProfileNameText.Text = name;

        ProfileAvatarInitial.IsVisible = isInitial;
        ProfileAvatarInitial.Text = initial;
        ProfileAvatarEmoji.IsVisible = isEmoji;
        ProfileAvatarEmoji.Text = settings.AvatarEmoji;
        ProfileAvatarMemoji.IsVisible = isMemoji;

        TopBarAvatarInitial.IsVisible = isInitial;
        TopBarAvatarInitial.Text = initial;
        TopBarAvatarEmoji.IsVisible = isEmoji;
        TopBarAvatarEmoji.Text = settings.AvatarEmoji;
        TopBarAvatarMemoji.IsVisible = isMemoji;

        if (isMemoji) _ = LoadAppliedMemojiAsync(settings.AvatarMemojiSeed);
    }

    /// <summary>Fetches the Tapback avatar and applies it to both places it's
    /// shown (top-bar button and Profile card), once it's downloaded/cached.</summary>
    private async Task LoadAppliedMemojiAsync(string seed)
    {
        var bitmap = await AvatarService.GetMemojiAsync(seed);
        if (bitmap is null) return;
        ProfileAvatarMemoji.Source = bitmap;
        TopBarAvatarMemoji.Source = bitmap;
    }

    private void OpenSettings_Click(object? sender, RoutedEventArgs e)
    {
        // Reflect whatever theme is currently active without re-triggering a save.
        _suppressThemeSave = true;
        var isLight = Application.Current!.ActualThemeVariant == ThemeVariant.Light;
        ThemeLightOption.IsChecked = isLight;
        ThemeDarkOption.IsChecked = !isLight;
        PositionThemeIndicator(isLight ? ThemeLightOption : ThemeDarkOption);
        _suppressThemeSave = false;

        SettingsOverlay.IsVisible = true;
    }

    private void CloseSettings_Click(object? sender, RoutedEventArgs e) => SettingsOverlay.IsVisible = false;
    private void SettingsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => SettingsOverlay.IsVisible = false;
    private void SettingsPanel_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    private bool _suppressThemeSave;

    private void ThemeOption_Checked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control target) return;
        PositionThemeIndicator(target);

        if (_suppressThemeSave) return;

        var theme = sender == ThemeLightOption ? "Light" : "Dark";
        Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;

        var settings = SettingsService.Load();
        settings.Theme = theme;
        SettingsService.Save(settings);
    }

    /// <summary>Slides the glass highlight behind whichever theme option is active
    /// (same idea as the nav pill, but with a known fixed 2-up layout).</summary>
    private void PositionThemeIndicator(Control target)
    {
        if (ThemeIndicator is null) return;
        var x = target == ThemeLightOption ? 100 : 0;
        ThemeIndicator.RenderTransform = new TranslateTransform(x, 0);
    }

    /// <summary>
    /// Opens a native file picker filtered to executables and Windows shortcuts,
    /// then adds the chosen file as a new game and selects it in the sidebar.
    /// </summary>
    private async void AddGame_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a game",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Executables and shortcuts")
                {
                    Patterns = new[] { "*.exe", "*.lnk" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        var path = file.Path.LocalPath;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);

        var entry = new GameEntry
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Untitled Game" : name,
            ExecutablePath = path,
            ColorHex = Palette[_nextColorIndex % Palette.Length]
        };
        _nextColorIndex++;

        _selectedEntry = entry; // so EnsureSelection (run by the CollectionChanged handler below) picks this one
        _games.Add(entry);
        GameLibrary.Save(_games);
    }

    // ===================== Scan for Games (Steam / Epic import) =====================

    private readonly List<(ScannedGame Game, CheckBox CheckBox)> _scanRows = new();

    /// <summary>Opens the scan overlay and kicks off a background scan of Steam and
    /// Epic Games Launcher libraries, populating the checklist as results come in.</summary>
    private async void ScanGames_Click(object? sender, RoutedEventArgs e)
    {
        ScanOverlay.IsVisible = true;
        ScanResultsPanel.Children.Clear();
        _scanRows.Clear();
        ScanSelectAllButton.IsVisible = false;
        ScanAddSelectedButton.IsEnabled = false;
        ScanStatusText.Text = "Looking for Steam and Epic Games Launcher libraries...";

        var found = await GameScannerService.ScanAsync();

        // Don't offer games that are already in the library — match on the launch
        // target, since that's what we'd otherwise add as a duplicate.
        var existingTargets = _games.Select(g => g.ExecutablePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newGames = found.Where(g => !existingTargets.Contains(g.LaunchUri)).ToList();

        if (newGames.Count == 0)
        {
            ScanStatusText.Text = found.Count == 0
                ? "No Steam or Epic Games Launcher installs were found on this PC."
                : "No new games found — everything scanned is already in your library.";
            return;
        }

        ScanStatusText.Text = $"Found {newGames.Count} game{(newGames.Count == 1 ? "" : "s")}. Choose which to add:";
        ScanSelectAllButton.IsVisible = true;

        foreach (var game in newGames.OrderBy(g => g.Name))
        {
            var checkBox = new CheckBox
            {
                Content = $"{game.Name}  ·  {game.Source}",
                IsChecked = true,
                Margin = new Thickness(2, 4, 2, 4)
            };
            checkBox.IsCheckedChanged += (_, _) => UpdateScanAddButtonState();
            _scanRows.Add((game, checkBox));
            ScanResultsPanel.Children.Add(checkBox);
        }

        UpdateScanAddButtonState();
    }

    private void UpdateScanAddButtonState()
        => ScanAddSelectedButton.IsEnabled = _scanRows.Any(r => r.CheckBox.IsChecked == true);

    private void ScanSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        var selectAll = _scanRows.Any(r => r.CheckBox.IsChecked != true);
        foreach (var (_, checkBox) in _scanRows) checkBox.IsChecked = selectAll;
        ScanSelectAllButton.Content = selectAll ? "Deselect All" : "Select All";
    }

    /// <summary>Adds every checked result as a new GameEntry, pointed at the
    /// platform's own launch URI (steam:// / com.epicgames.launcher://) rather than
    /// a raw .exe path, so Steam/Epic themselves resolve the correct executable.</summary>
    private void AddScannedGames_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var (game, checkBox) in _scanRows)
        {
            if (checkBox.IsChecked != true) continue;

            _games.Add(new GameEntry
            {
                Name = game.Name,
                ExecutablePath = game.LaunchUri,
                Platform = "PC (Windows)",
                ColorHex = Palette[_nextColorIndex % Palette.Length]
            });
            _nextColorIndex++;
        }

        GameLibrary.Save(_games);
        ScanOverlay.IsVisible = false;
    }

    private void CloseScan_Click(object? sender, RoutedEventArgs e) => ScanOverlay.IsVisible = false;
    private void ScanBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => ScanOverlay.IsVisible = false;
    private void ScanPanel_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;

    // ===================== Details editor (opened via the pencil icon or right-click) =====================

    private void OpenDetails(GameEntry entry)
    {
        _editingEntry = entry;
        _pendingCoverPath = entry.CoverImagePath;
        _pendingBackgroundPath = entry.BackgroundImagePath;

        DetailName.Text = entry.Name;
        DetailGenre.Text = entry.Genre;
        DetailDescription.Text = entry.Description;
        DetailNotes.Text = entry.Notes;
        DetailReleaseDate.Text = entry.ReleaseDate;
        DetailPlatform.Text = entry.Platform;

        DetailHeaderName.Text = string.IsNullOrWhiteSpace(entry.Name) ? "Game name" : entry.Name;
        UpdateHeaderMeta();

        UpdateCoverPreview();
        UpdateBackgroundPreview();
        AutoFillStatus.IsVisible = false;
        AutoFillStatus.Text = "";
        DetailsOverlay.IsVisible = true;
    }

    private void DetailHeader_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_editingEntry is null) return;
        DetailHeaderName.Text = string.IsNullOrWhiteSpace(DetailName.Text) ? "Game name" : DetailName.Text;
        UpdateHeaderMeta();
    }

    private void UpdateHeaderMeta()
    {
        var platform = string.IsNullOrWhiteSpace(DetailPlatform.Text) ? "PC (Windows)" : DetailPlatform.Text;
        var release = string.IsNullOrWhiteSpace(DetailReleaseDate.Text) ? "Not Played" : DetailReleaseDate.Text;
        DetailHeaderMeta.Text = $"{release} · {platform}";
    }

    private void UpdateCoverPreview()
    {
        if (!string.IsNullOrWhiteSpace(_pendingCoverPath) && System.IO.File.Exists(_pendingCoverPath))
        {
            try
            {
                DetailCoverImage.Source = new Avalonia.Media.Imaging.Bitmap(_pendingCoverPath);
                DetailCoverFallback.IsVisible = false;
                return;
            }
            catch
            {
                // fall through to fallback background below
            }
        }

        DetailCoverImage.Source = null;
        DetailCoverFallback.IsVisible = true;
    }

    /// <summary>Same lazy-preview pattern as the cover, for the wide backdrop image
    /// shown behind the details header.</summary>
    private void UpdateBackgroundPreview()
    {
        if (!string.IsNullOrWhiteSpace(_pendingBackgroundPath) && System.IO.File.Exists(_pendingBackgroundPath))
        {
            try
            {
                DetailBackgroundImage.Source = new Avalonia.Media.Imaging.Bitmap(_pendingBackgroundPath);
                DetailBackgroundFallback.IsVisible = false;
                return;
            }
            catch
            {
                // fall through to fallback background below
            }
        }

        DetailBackgroundImage.Source = null;
        DetailBackgroundFallback.IsVisible = true;
    }

    private List<Services.GameMetadataCandidate> _metadataCandidates = new();

    /// <summary>
    /// Searches Steam and PSN for the current Name field and opens a picker
    /// flyout so the user chooses the right match instead of us guessing the top result.
    /// </summary>
    private async void AutoFillMetadata_Click(object? sender, RoutedEventArgs e)
    {
        var query = DetailName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            AutoFillStatus.Text = "Type a game name first.";
            AutoFillStatus.IsVisible = true;
            return;
        }

        AutoFillButton.IsEnabled = false;
        AutoFillStatus.IsVisible = true;
        AutoFillStatus.Text = $"Searching Steam and PSN for \u201c{query}\u201d\u2026";

        _metadataCandidates = await Services.MetadataService.SearchAsync(query);
        MetadataResultsList.ItemsSource = _metadataCandidates;

        AutoFillButton.IsEnabled = true;

        if (_metadataCandidates.Count == 0)
        {
            AutoFillStatus.Text = "No match found. Try a more exact title.";
            return;
        }

        AutoFillStatus.Text = $"Found {_metadataCandidates.Count} match(es) — pick one below.";
        AutoFillButton.Flyout?.ShowAt(AutoFillButton);
    }

    /// <summary>User picked a candidate from the flyout list — fetch its full details and fill the form.</summary>
    private async void MetadataResultsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (MetadataResultsList.SelectedItem is not Services.GameMetadataCandidate candidate) return;

        AutoFillButton.Flyout?.Hide();
        AutoFillButton.IsEnabled = false;
        AutoFillStatus.IsVisible = true;
        AutoFillStatus.Text = $"Fetching details from {candidate.Source}\u2026";

        var metadata = await Services.MetadataService.FetchDetailsAsync(candidate);

        MetadataResultsList.SelectedItem = null; // reset so picking the same item again still fires

        if (metadata is null)
        {
            AutoFillStatus.Text = $"Couldn't fetch details from {candidate.Source}. Try another match.";
            AutoFillButton.IsEnabled = true;
            return;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Name)) DetailName.Text = metadata.Name;
        if (!string.IsNullOrWhiteSpace(metadata.Genre)) DetailGenre.Text = metadata.Genre;
        if (!string.IsNullOrWhiteSpace(metadata.ReleaseDate)) DetailReleaseDate.Text = metadata.ReleaseDate;
        if (!string.IsNullOrWhiteSpace(metadata.Platform)) DetailPlatform.Text = metadata.Platform;
        if (!string.IsNullOrWhiteSpace(metadata.Description)) DetailDescription.Text = metadata.Description;

        if (!string.IsNullOrWhiteSpace(metadata.CoverImagePath))
        {
            _pendingCoverPath = metadata.CoverImagePath;
            UpdateCoverPreview();
        }

        if (!string.IsNullOrWhiteSpace(metadata.BackgroundImagePath))
        {
            _pendingBackgroundPath = metadata.BackgroundImagePath;
            UpdateBackgroundPreview();
        }

        UpdateHeaderMeta();
        DetailHeaderName.Text = string.IsNullOrWhiteSpace(DetailName.Text) ? "Game name" : DetailName.Text;

        AutoFillStatus.Text = $"Filled from {candidate.Source}. Review and Save.";
        AutoFillButton.IsEnabled = true;
    }

    private async void ChangeCover_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a cover image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        _pendingCoverPath = file.Path.LocalPath;
        UpdateCoverPreview();
    }

    /// <summary>Opens a native file picker for the wide backdrop image, independent of
    /// the square cover art used in tiles.</summary>
    private async void ChangeBackground_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a background image",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Images")
                {
                    Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                }
            }
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        _pendingBackgroundPath = file.Path.LocalPath;
        UpdateBackgroundPreview();
    }

    private void SaveDetails_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingEntry is null) return;

        _editingEntry.Name = string.IsNullOrWhiteSpace(DetailName.Text) ? _editingEntry.Name : DetailName.Text;
        _editingEntry.Genre = DetailGenre.Text ?? "";
        _editingEntry.Description = DetailDescription.Text ?? "";
        _editingEntry.Notes = DetailNotes.Text ?? "";
        _editingEntry.ReleaseDate = DetailReleaseDate.Text ?? "";
        _editingEntry.Platform = string.IsNullOrWhiteSpace(DetailPlatform.Text) ? "PC (Windows)" : DetailPlatform.Text;
        _editingEntry.CoverImagePath = _pendingCoverPath;
        _editingEntry.BackgroundImagePath = _pendingBackgroundPath;

        GameLibrary.Save(_games);
        ApplyFilter();
        CloseDetails();
    }

    private void CancelDetails_Click(object? sender, RoutedEventArgs e) => CloseDetails();

    private void RemoveGame_Click(object? sender, RoutedEventArgs e)
    {
        if (_editingEntry is null) return;
        _games.Remove(_editingEntry);
        GameLibrary.Save(_games);
        CloseDetails();
    }

    private void CloseDetails()
    {
        var editedEntry = _editingEntry;
        _editingEntry = null;
        _pendingCoverPath = null;
        _pendingBackgroundPath = null;
        DetailsOverlay.IsVisible = false;

        // Refresh the detail pane immediately if the game just edited is the one on screen.
        if (editedEntry is not null && editedEntry == _selectedEntry)
            ShowSelectedGame(editedEntry);
    }

    /// <summary>Clicking the dimmed backdrop (outside the panel) cancels, same as the ✕ button.</summary>
    private void DetailsBackdrop_PointerPressed(object? sender, PointerPressedEventArgs e) => CloseDetails();

    /// <summary>Stops clicks inside the panel from bubbling up to the backdrop handler above.</summary>
    private void DetailsPanel_PointerPressed(object? sender, PointerPressedEventArgs e) => e.Handled = true;
}
