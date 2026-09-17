using System.Text.Json.Serialization;

namespace FolderTreeMD.Core;

/// <summary>
/// The persisted application settings. The properties mirror the settings schema of
/// <c>UI_SPEC.md</c> §4 exactly — same eight values, same JSON key names — so the file on disk is
/// the schema and nothing else.
/// </summary>
// LEARN[11]: persistence uses System.Text.Json with an explicit [JsonPropertyName] on every
// property, and SettingsStore.Load never throws.
// Alternatives considered: (a) a camelCase naming policy on JsonSerializerOptions — rejected: with
//   attributes the mapping is visible at the property it belongs to, survives a rename (the
//   compiler keeps attribute and property together) and cannot be broken by a forgotten options
//   argument at one call site; (b) XML or an INI file — rejected: §4 fixes JSON, and the .NET 8
//   BCL already has a JSON serializer, so no dependency is needed; (c) letting a parse failure throw
//   and having the UI catch it — rejected: §4 says a missing or corrupt file silently means
//   defaults, and an exception on startup would be a crash unless every caller remembered to catch.
// Pros of chosen approach: the disk format is pinned by the same file that defines the properties;
//   a corrupt file is a normal, tested path rather than an error path.
// Cons of chosen approach: eight attributes to keep in step with the properties (a new property
//   without one would silently change the on-disk key), which is why SettingsStoreTests asserts the
//   exact key set that is written.
// See also: LEARN[12]
public class AppSettings
{
    /// <summary>Recursion depth: <c>-1</c> = unlimited, <c>0</c> = root line only (§4).</summary>
    [JsonPropertyName("depth")]
    public int Depth { get; set; } = -1;

    /// <summary>Whether hidden entries are listed and counted.</summary>
    [JsonPropertyName("includeHidden")]
    public bool IncludeHidden { get; set; }

    /// <summary>Whether directory symlinks/junctions are followed.</summary>
    [JsonPropertyName("followSymlinks")]
    public bool FollowSymlinks { get; set; }

    /// <summary>Whether file lines get a size suffix. Default on (§4).</summary>
    [JsonPropertyName("showFileSizes")]
    public bool ShowFileSizes { get; set; } = true;

    /// <summary>Whether folder lines get a recursive size suffix. Default off (§4).</summary>
    [JsonPropertyName("showFolderSizes")]
    public bool ShowFolderSizes { get; set; }

    /// <summary>Whether lines get the <c>[HRSA]</c> flag group. Default on (§4).</summary>
    [JsonPropertyName("showAttributes")]
    public bool ShowAttributes { get; set; } = true;

    /// <summary>Spaces per level; only <c>2</c> or <c>4</c> are valid, default <c>4</c> (§4).</summary>
    [JsonPropertyName("indentSize")]
    public int IndentSize { get; set; } = 4;

    /// <summary>Last folder the user selected, restored into the path box without auto-generating (§4).</summary>
    [JsonPropertyName("lastFolder")]
    public string? LastFolder { get; set; }
}
