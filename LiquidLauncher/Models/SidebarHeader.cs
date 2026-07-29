namespace LiquidLauncher.Models;

/// <summary>
/// A section label mixed into the sidebar's item list alongside GameEntry rows
/// (e.g. "Favorites (3)", "Playing (11)") so the list reads as grouped sections
/// the way native macOS sidebars do, without needing a full CollectionView/
/// grouping setup. Rendered via its own DataTemplate; SelectionChanged reverts
/// the selection if one of these is clicked, since headers aren't real rows.
/// </summary>
public class SidebarHeader
{
    public required string Title { get; init; }
}
