namespace CitySim.Content;

/// <summary>
/// A content file that isn't a card or a tray entry but is still looked up by id (a road style). Any <c>.tres</c> whose
/// script implements this is indexed by <see cref="ContentLibrary"/> per type, with the same replace-by-id rules.
/// </summary>
public interface IContent
{
    /// <summary>Stable id, unique per type. A mod file with the same id replaces this one.</summary>
    string Id { get; }
    /// <summary>Where the file came from: "Base" or the mod folder's name. Set when loaded.</summary>
    string Source { get; set; }
}
