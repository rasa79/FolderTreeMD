using System.IO;
using System.Text;
using System.Text.Json;

namespace FolderTreeMD.Core;

/// <summary>
/// Reads and writes <see cref="AppSettings"/> as JSON at the location fixed by <c>UI_SPEC.md</c> §4:
/// <c>%APPDATA%\FolderTreeMD\settings.json</c>.
/// </summary>
/// <remarks>
/// The contract is deliberately forgiving in one direction and strict in the other: <see cref="Load"/>
/// never throws (a missing, unreadable or corrupt file is the defaults, with no dialog — §4), while
/// <see cref="Save"/> reports an I/O failure to its caller instead of hiding it, because a settings
/// change the user made should not be lost silently.
/// </remarks>
// LEARN[12]: the settings file path and the JSON are the whole persistence layer; there is no
// caching, no change notification and no settings service.
// Alternatives considered: (a) a settings service registered at startup and injected into the window
//   — rejected for M4 as an abstraction with a single consumer (karpathy-guidelines §2); the window
//   loads once, saves on change, and the future CLI (M6) calls the same two methods; (b) writing on
//   exit instead of on every change — rejected: §3.3 says toggles persist immediately and there is no
//   Save button, and a crash would otherwise lose the last edits; (c) storing the values in the
//   registry — rejected: §4 fixes a JSON file in %APPDATA%.
// Pros of chosen approach: two stateless methods, no lifetime to manage, trivially testable against a
//   temp path, and the same entry points serve the UI (M4) and the CLI (M6).
// Cons of chosen approach: every change re-serialises the whole file (eight small values — measured
//   cost is irrelevant), and callers must handle the failure that Save can report.
// See also: LEARN[11]
public static class SettingsStore
{
    /// <summary>The eight valid keys of the §4 schema, in the order the schema lists them.</summary>
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>
    /// Full path of the settings file: <c>%APPDATA%\FolderTreeMD\settings.json</c> (§4).
    /// </summary>
    public static string SettingsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FolderTreeMD",
        "settings.json");

    /// <summary>
    /// Loads the settings, falling back to defaults for a missing, unreadable or corrupt file and
    /// coercing invalid values (§4). Never throws.
    /// </summary>
    /// <returns>The loaded settings, or defaults.</returns>
    public static AppSettings Load() => LoadFrom(SettingsFilePath);

    /// <summary>
    /// Saves the settings to <see cref="SettingsFilePath"/>, creating the directory if needed.
    /// </summary>
    /// <param name="settings">Settings to persist.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The location is not writable.</exception>
    public static void Save(AppSettings settings) => SaveTo(settings, SettingsFilePath);

    /// <summary>
    /// Loads settings from a specific path. Internal so tests can use a temp file instead of the
    /// user's real profile; the public API keeps the single §4 location.
    /// </summary>
    /// <param name="path">Path of the settings file.</param>
    /// <returns>The loaded settings, or defaults.</returns>
    internal static AppSettings LoadFrom(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new AppSettings();
            }

            AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path));
            return Normalize(loaded ?? new AppSettings());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // §4: missing/corrupt → defaults, no error dialog. The user cannot act on a broken
            // settings file, and refusing to start would be worse than starting with defaults.
            return new AppSettings();
        }
    }

    /// <summary>
    /// Saves settings to a specific path, creating the parent directory. Internal for the same reason
    /// as <see cref="LoadFrom"/>.
    /// </summary>
    /// <param name="settings">Settings to persist.</param>
    /// <param name="path">Path of the settings file.</param>
    // LEARN[15] (D15): the write is atomic — a uniquely named temp file in the same directory, then
    // File.Move(..., overwrite: true) — instead of truncating the settings file in place.
    // Alternatives considered: (a) File.WriteAllText directly on the target (the first M4 version) —
    //   rejected: a crash or power loss mid-write leaves a truncated file, and because §4 maps any
    //   unreadable content to defaults, the user would silently lose all eight settings; (b) writing
    //   to the temp directory (Path.GetTempPath) and moving across volumes — rejected: File.Move is
    //   only atomic within one volume, and %TEMP% is frequently on another drive; (c) File.Replace —
    //   rejected: it needs the destination to exist already, so a first save would need a separate
    //   path anyway.
    // Pros of chosen approach: the destination is either the previous file or the complete new one, so
    //   a crash cannot produce a half-written settings.json; the temp file is in the same directory, so
    //   the move is a same-volume rename.
    // Cons of chosen approach: one extra file exists for the duration of the write, and a failure after
    //   the temp write (but before the move) would leave it behind — which is why the catch deletes it
    //   and SettingsStoreTests asserts that no residue remains.
    // See also: LEARN[11]
    internal static void SaveTo(AppSettings settings, string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string json = JsonSerializer.Serialize(settings, WriteOptions);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            // UTF-8 without BOM, matching the listing files this app writes (§5 rule 12).
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            // Never leave a partial temp file next to the settings file; the original error stands.
            TryDelete(temporaryPath);
            throw;
        }
    }

    /// <summary>Deletes a file if it is there, ignoring the failures a cleanup attempt may hit.</summary>
    /// <param name="path">Path to delete.</param>
    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Brings a loaded settings object into the range the schema allows: <c>indentSize</c> is only
    /// <c>2</c> or <c>4</c>, anything else becomes the default <c>4</c> (§4).
    /// </summary>
    /// <param name="settings">Settings as read from disk.</param>
    /// <returns>The same instance, coerced.</returns>
    private static AppSettings Normalize(AppSettings settings)
    {
        if (settings.IndentSize is not (2 or 4))
        {
            settings.IndentSize = 4;
        }

        return settings;
    }
}
