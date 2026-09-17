namespace FolderTreeMD.Core;

/// <summary>Which Explorer surface a classic-menu registration targets.</summary>
public enum ShellSurface
{
    /// <summary>Right-clicking a folder icon.</summary>
    FolderIcon,

    /// <summary>Right-clicking the empty space inside a folder view.</summary>
    FolderBackground,
}

/// <summary>
/// One registry value a registration needs: the key path, the value name (<c>null</c> for the key's default
/// value) and the value to write.
/// </summary>
/// <param name="KeyPath">Key path below <c>HKEY_CURRENT_USER</c>.</param>
/// <param name="ValueName">Value name, or <c>null</c> for the key's default value.</param>
/// <param name="Value">Value to write.</param>
public sealed record ShellRegistryValue(string KeyPath, string? ValueName, string Value);

/// <summary>
/// The registry work a portable install needs, as data: the values to write (in order) and the keys to remove
/// on uninstall. Holding it as data is what makes the registration testable without touching a registry.
/// </summary>
public sealed class ShellRegistrationPlan
{
    /// <summary>The executable the registration points at.</summary>
    public string ExecutablePath { get; }

    /// <summary>Every value the registration needs, in write order.</summary>
    public IReadOnlyList<ShellRegistryValue> Values { get; }

    /// <summary>The verb keys to remove on uninstall (their <c>command</c> subkeys go with them).</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Creates a plan. Not public: a plan comes from <see cref="ShellRegistration.Plan"/>.</summary>
    /// <param name="executablePath">The executable the registration points at.</param>
    /// <param name="values">The values to write.</param>
    /// <param name="keys">The keys to remove on uninstall.</param>
    internal ShellRegistrationPlan(string executablePath, IReadOnlyList<ShellRegistryValue> values, IReadOnlyList<string> keys)
    {
        ExecutablePath = executablePath;
        Values = values;
        Keys = keys;
    }

    /// <summary>
    /// Whether the registry already holds this plan, asked through a reader the caller supplies — the whole
    /// point of keeping the registration as data: the idempotence rule is tested without a registry.
    /// </summary>
    /// <param name="readValue">
    /// Reads one value: (key path, value name) → the current value, or <c>null</c> when the key or the value
    /// is absent.
    /// </param>
    /// <returns>Whether every planned value is already present and identical.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="readValue"/> is <c>null</c>.</exception>
    public bool IsUpToDate(Func<string, string?, string?> readValue)
    {
        ArgumentNullException.ThrowIfNull(readValue);

        foreach (ShellRegistryValue value in Values)
        {
            // Ordinal: the registry stores exactly what was written, and a case-insensitive comparison would
            // call a path that differs only in case "up to date" although the shell would run the other path.
            if (!string.Equals(readValue(value.KeyPath, value.ValueName), value.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// The classic (per-user) Explorer entry a portable install writes, computed as pure data: which keys and
/// values under <c>HKEY_CURRENT_USER\Software\Classes</c> point the shell at this executable.
/// </summary>
// LEARN[35] (M8, DECISIONS.md D28): the portable install's registry work is a pure plan in Core, and the
// application side only reads and writes what the plan says.
// Why: the entry is the milestone's user-visible contract ("copy the exe, run it once"), and the parts that
//   can be wrong are the strings and the paths — the command line's quoting, the two placeholders (%1 for a
//   folder icon, %V for a folder view's background), the icon reference, and the two key paths. Those are
//   exactly what a test can pin, but only if they are computed without a registry in reach: the Core test
//   project targets net8.0 and references Core alone (LEARN[1], D4), and Microsoft.Win32.Registry is a
//   Windows-TFM API, so any registry call inside Core would make the plan untestable and would need a
//   package reference. The plan is therefore data (ShellRegistryValue/ShellRegistrationPlan) and the
//   comparison against the registry is a delegate the caller supplies (IsUpToDate).
// Alternatives considered: (a) put the whole registration in the app and test it by writing to HKCU in the
//   test project — impossible across the TFM boundary, and a test that mutates the machine's registry is not
//   a test a gate can run repeatedly; (b) add the Microsoft.Win32.Registry package to Core — rejected:
//   AGENTS.md forbids new dependencies, and it would not make the *strings* testable either, only the calls;
//   (c) write the keys with a .reg file the user double-clicks — rejected: the human asked the exe to
//   register itself, and a .reg file cannot know where the exe sits after a move; (d) an MSIX-only path
//   (M7) — rejected: it needs a certificate, machine-wide trust and a sign-out, which is the opposite of
//   "copy the exe and run it".  See also: LEARN[30] (the same layering decision for the §7 grammar), D28.
public static class ShellRegistration
{
    /// <summary>The verb key's name under both surfaces.</summary>
    public const string VerbKeyName = "FolderTreeMD";

    /// <summary>The label the classic menu shows.</summary>
    public const string Label = "Copy folder content as markdown";

    /// <summary>Placeholder the shell replaces with the clicked folder.</summary>
    public const string FolderPlaceholder = "%1";

    /// <summary>Placeholder the shell replaces with the folder a background click happened in.</summary>
    public const string BackgroundPlaceholder = "%V";

    /// <summary>Key path (below <c>HKEY_CURRENT_USER</c>) of the folder-icon verb.</summary>
    public const string FolderIconKeyPath = @"Software\Classes\Directory\shell\" + VerbKeyName;

    /// <summary>Key path (below <c>HKEY_CURRENT_USER</c>) of the folder-background verb.</summary>
    public const string FolderBackgroundKeyPath = @"Software\Classes\Directory\Background\shell\" + VerbKeyName;

    /// <summary>The <c>command</c> subkey, appended to a verb key, that holds the command line.</summary>
    public const string CommandSubKeyName = "command";

    /// <summary>The <c>Icon</c> value's name.</summary>
    public const string IconValueName = "Icon";

    /// <summary>
    /// The package name (<c>Identity/@Name</c> in the manifest, <c>Package.Id.Name</c> at runtime) whose
    /// processes must not write the portable keys. The **name**, not the family name: the family name carries
    /// the publisher hash, so re-creating the signing certificate would change it and silently break the rule.
    /// </summary>
    public const string PackagedIdentityName = "FolderTreeMD.Sparse";

    /// <summary>
    /// Whether a process that belongs to <paramref name="packageName"/> must skip the portable
    /// self-registration — true only for this app's own package, and false for every other answer, including
    /// <c>null</c> (no package identity, or the identity could not be read).
    /// </summary>
    /// <param name="packageName">The process's package name, or <c>null</c> when there is none.</param>
    /// <returns>Whether the portable self-registration must be skipped.</returns>
    // LEARN[39] (M8 fix round 3, closing L34 and L35): the skip decision is a pure function over the package
    // *name*, not over "has any package identity".
    // Why: package identity is inherited, so "has identity" is true for a portable copy started from any
    //   packaged host (a Store-build terminal, a packaged editor's terminal) — the case L34 recorded, where the
    //   portable copy silently stopped registering. Comparing the name answers the question that matters ("is
    //   this process part of *this* app's package?") and is testable without WinRT, because the decision is
    //   data-in/bool-out and the reading of `Package.Current.Id.Name` stays in the app. The name is also the
    //   stable half of the identity: `Package.Id.FamilyName` embeds a publisher hash, so re-creating the
    //   signing certificate would change it and quietly turn the skip into a register.
    // Alternatives considered: (a) "Package.Current succeeds" — rejected: too broad, exactly L34; (b) the
    //   family name — rejected for the certificate reason above; (c) reading the name in the app and comparing
    //   there — rejected: the comparison is the part that can be wrong, and the Core-only suite can test a pure
    //   function but cannot reach the WPF project (D4, LEARN[1]); (d) skipping on any *error* while reading the
    //   identity — rejected by the human's rule: a detection failure means "register", the portable direction.
    // See also: LEARN[36], LEARN[38], D28
    public static bool ShouldSkipRegistration(string? packageName) =>
        string.Equals(packageName, PackagedIdentityName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the verb key path for a surface.</summary>
    /// <param name="surface">Surface to register.</param>
    /// <returns>The key path below <c>HKEY_CURRENT_USER</c>.</returns>
    public static string KeyPath(ShellSurface surface) => surface switch
    {
        ShellSurface.FolderIcon => FolderIconKeyPath,
        ShellSurface.FolderBackground => FolderBackgroundKeyPath,
        _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "unknown shell surface"),
    };

    /// <summary>Returns the command line a surface runs, quoted exactly as the registry needs it.</summary>
    /// <param name="executablePath">Absolute path of the executable to register.</param>
    /// <param name="surface">Surface to register.</param>
    /// <returns>The command line, e.g. <c>"C:\tools\FolderTreeMD.exe" --folder "%1"</c>.</returns>
    public static string Command(string executablePath, ShellSurface surface)
    {
        string placeholder = surface switch
        {
            ShellSurface.FolderIcon => FolderPlaceholder,
            ShellSurface.FolderBackground => BackgroundPlaceholder,
            _ => throw new ArgumentOutOfRangeException(nameof(surface), surface, "unknown shell surface"),
        };

        // The §7 option name comes from CliCommand, so the registration can never drift from the grammar the
        // app actually parses (LEARN[30]).
        return $"\"{executablePath}\" {CliCommand.FolderOption} \"{placeholder}\"";
    }

    /// <summary>
    /// Builds the registration for both surfaces: a label, the executable's icon and a command line per verb
    /// key. The same executable can be registered from anywhere, which is what makes the portable copy work.
    /// </summary>
    /// <param name="executablePath">Absolute path of the executable to register.</param>
    /// <returns>The plan: values to write, and the keys to remove on uninstall.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="executablePath"/> is blank, is not a fully qualified path (a drive-relative
    /// <c>C:tools\x.exe</c> and a root-relative <c>\tools\x.exe</c> are refused too — <c>Path.IsPathRooted</c>
    /// would have accepted both, OCR M8 finding 6), or ends with a directory separator (a directory was
    /// passed where the executable belongs).
    /// </exception>
    public static ShellRegistrationPlan Plan(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException($"the executable path must be fully qualified: '{executablePath}'", nameof(executablePath));
        }

        string[] separators = [Path.DirectorySeparatorChar.ToString(), Path.AltDirectorySeparatorChar.ToString()];
        if (separators.Contains(executablePath[^1..]))
        {
            throw new ArgumentException($"the executable path must name a file, not a directory: '{executablePath}'", nameof(executablePath));
        }

        var values = new List<ShellRegistryValue>();
        var keys = new List<string>();

        foreach (ShellSurface surface in new[] { ShellSurface.FolderIcon, ShellSurface.FolderBackground })
        {
            string keyPath = KeyPath(surface);
            keys.Add(keyPath);

            // The verb key's default value is the menu label the shell shows.
            values.Add(new ShellRegistryValue(keyPath, null, Label));
            // ",0" selects the first icon group in the executable — the app icon set by ApplicationIcon.
            values.Add(new ShellRegistryValue(keyPath, IconValueName, $"{executablePath},0"));
            // The command lives in a subkey whose default value is the line the shell runs.
            values.Add(new ShellRegistryValue($@"{keyPath}\{CommandSubKeyName}", null, Command(executablePath, surface)));
        }

        return new ShellRegistrationPlan(executablePath, values, keys);
    }
}
