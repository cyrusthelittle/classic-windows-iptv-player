namespace ClassicWindowsIptvPlayer.Windows;

internal static class LibraryNavigationPolicy
{
    public static string EmptyMessage(int catalogCount, string search, int viewMode)
    {
        if (catalogCount == 0) return "No library is available. Retry the provider or open a saved library from Playlist.";
        if (!string.IsNullOrWhiteSpace(search)) return "No results in this scope. Clear search or reset filters.";
        return viewMode switch
        {
            1 => "No favorites in this scope. Select an item and add it to favorites, or reset filters.",
            2 => "No recently played items in this scope. Play an item to add it here, or reset filters.",
            _ => "No items in this scope. Reset filters to browse the library."
        };
    }

    public static bool CanReset(int viewMode, int browseMode, bool hasDrilldown, string search) =>
        viewMode != 0 || browseMode != 0 || hasDrilldown || !string.IsNullOrEmpty(search);
}

internal sealed record LibraryPosition(
    string? Folder, string? FavoriteFolder, string? Letter, int BrowseMode, int ViewMode,
    string? SelectedKey, double ScrollOffset);

internal sealed class LibraryNavigationHistory
{
    private readonly Dictionary<int, LibraryPosition> _positions = new();

    public LibraryPosition? Switch(int fromKind, int toKind, LibraryPosition current)
    {
        if (fromKind == toKind) return null;
        _positions[fromKind] = current;
        return _positions.GetValueOrDefault(toKind);
    }

    public static int SelectedIndex<T>(IReadOnlyList<T> entries, string? key, Func<T, string?> identity)
    {
        if (key is null) return -1;
        for (var i = 0; i < entries.Count; i++)
            if (string.Equals(identity(entries[i]), key, StringComparison.Ordinal)) return i;
        return -1;
    }
}

internal static class CatalogPositionRetention
{
    public static bool ShouldRestoreScroll(string previousScope, string currentScope, bool explicitRestore) =>
        !explicitRestore && string.Equals(previousScope, currentScope, StringComparison.Ordinal);
}
