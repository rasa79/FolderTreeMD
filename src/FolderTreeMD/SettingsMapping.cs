using FolderTreeMD.Core;

namespace FolderTreeMD;

/// <summary>
/// Maps the persisted settings of <c>UI_SPEC.md</c> §4 onto the engine's option object of §6.
/// </summary>
// LEARN[13]: settings are mapped to engine options in exactly one place, field per field, with a
// field-per-field initialiser rather than a shared base class or an implicit conversion.
// Alternatives considered: (a) let ListingOptions extend/duplicate AppSettings or hold one as a
//   property — rejected: it would couple the persisted schema to the engine's contract, so a
//   settings change (or a second consumer such as the CLI) could silently alter engine behaviour;
//   (b) an AutoMapper-style implicit conversion — rejected: a dependency and a layer of indirection
//   for seven assignments; (c) set ListingOptions properties from each handler — rejected: the
//   options object is short-lived per run, and this keeps one construction point.
// Pros: the mapping is auditable at a glance — every §4 key appears exactly once — and the UI and the
//   CLI (§7) share it.
// Cons: adding an option to both types means touching this method (two edits), which is precisely
//   the point at which a silent mis-wiring would be noticed.
// Revised in M6: moved out of MainWindow.xaml.cs's private static method into this file, because the
//   CLI needs the same mapping and duplicating it would defeat the decision above (LEARN[12] already
//   named the CLI as a second consumer of the persistence layer). No behaviour changed.
// See also: LEARN[11], LEARN[12]
internal static class SettingsMapping
{
    /// <summary>Builds the engine options for one run from the persisted settings.</summary>
    /// <param name="settings">Persisted settings.</param>
    /// <returns>Options for one engine run.</returns>
    internal static ListingOptions ToOptions(AppSettings settings) => new()
    {
        Depth = settings.Depth,
        IncludeHidden = settings.IncludeHidden,
        FollowSymlinks = settings.FollowSymlinks,
        ShowFileSizes = settings.ShowFileSizes,
        ShowFolderSizes = settings.ShowFolderSizes,
        ShowAttributes = settings.ShowAttributes,
        IndentSize = settings.IndentSize,
    };
}
