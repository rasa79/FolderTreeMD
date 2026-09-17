namespace FolderTreeMD.Core;

/// <summary>What a command line turned out to be, per <c>UI_SPEC.md</c> §7.</summary>
public enum CliCommandKind
{
    /// <summary>No CLI option was present: the process should start the window.</summary>
    NotACliRequest,

    /// <summary>A folder to list, with an optional output file (§7).</summary>
    ListFolder,

    /// <summary>A CLI request whose arguments are malformed: report <see cref="CliCommand.Error"/> and exit 1.</summary>
    UsageError,

    /// <summary>Register this executable's classic Explorer entries in HKCU, then exit (M8, D28).</summary>
    InstallShell,

    /// <summary>Remove those entries, then exit (M8, D28).</summary>
    UninstallShell,
}

/// <summary>
/// The §7 command line as a value: the parsed request, or the reason it is malformed. Parsing is pure —
/// it touches no filesystem and starts no process — so the §7 grammar is unit-testable.
/// </summary>
// LEARN[30] (M6 fix round, recorded as DECISIONS.md D24): the §7 argument scan lives in Core as a pure
// function instead of in App.xaml.cs.
// Why it moved: M6's first version kept a private static scan on App, so the one piece of M6 logic with
//   real boundary conditions (repeated options, valueless options, an option token as a value, unknown
//   arguments) had no automated coverage — the test project targets net8.0 and references Core only
//   (LEARN[1], D4), so nothing in the WPF project is reachable from the suite. OCR run 6 then found two
//   real defects in exactly that scan. Core is where the project already puts pure, testable decisions
//   it wants covered (D18's LongPathStatus, D19's ListingStats/ListingFile), so the scan follows them.
// Alternatives considered: (a) leave the scan on App and rely on manual CLI runs — rejected: that is
//   what produced the defects, and the milestone's DoD asks for evidence a machine can repeat;
//   (b) add a net8.0-windows test project just for this — rejected: a structural change to PLAN.md's
//   layout for one pure function (the reasoning recorded in D18/D19); (c) make the scan internal on App
//   and expose it via InternalsVisibleTo to the test project — impossible: InternalsVisibleTo cannot
//   cross an incompatible target framework, the test project cannot reference net8.0-windows at all.
// Pros: the grammar and its error wording are pinned by table-driven tests, and the wording of a usage
//   failure exists once, in the library that also owns the engine.
// Cons: three user-facing strings and a small type now live in the engine library — the same documented
//   layering concession as D18 (inert inside a pure function), and §7 grammar joins the library's
//   public surface.
// Carried from LEARN[28] (this scan's previous home in App.xaml.cs): the scan stays hand-rolled — §7 has
//   two options, they are unordered, and System.CommandLine or another parser package would be a
//   dependency bought for nothing. The value semantics tightened in this round (an option token is never
//   consumed as a value; a valueless option is a usage failure), and LEARN[28]'s note that a valueless
//   "--folder" is indistinguishable from a missing one no longer holds: the parser now reports which of
//   the two it saw, and a valueless "--to" is an error instead of a silent fallback to the clipboard.
// Second tightening (M6 fix round 2, OCR run-2 finding 3): a known option in the single-token
//   "--name=value" shape is a usage failure rather than an unknown argument. Rejecting the form was
//   already D23's decision, but *ignoring* it was a different thing with a worse outcome — the token was
//   dropped, so "--folder C:\data --to=C:\out.md" copied to the clipboard and exited 0 for a caller who
//   asked for a file, and "--folder=C:\data" alone fell through to the window request. Only a full,
//   case-insensitive name match followed by '=' counts, so "--other=value" and "--folderx=1" remain
//   ordinary unknown arguments (which M7's activation argument needs).
// Third tightening (M6 fix round 3, OCR run-3 finding 2): that check made the scan dereference an element
//   before comparing it, so a null element in the array threw NullReferenceException where the older
//   string.Equals-based scan had simply skipped it — a behaviour change in a public API (D24 made the §7
//   grammar part of the library's surface). The helper and the value take are null-tolerant again: a null
//   element outside a value position is an unknown argument, and a null in a value position is "no usable
//   value" (a usage failure naming that option) rather than a crash.
// See also: LEARN[26], LEARN[28], LEARN[16]
public sealed class CliCommand
{
    /// <summary>Option that selects CLI mode and names the folder to list.</summary>
    public const string FolderOption = "--folder";

    /// <summary>Optional option naming the output file.</summary>
    public const string OutputOption = "--to";

    /// <summary>Registers this executable's classic Explorer entries in HKCU (M8).</summary>
    public const string InstallOption = "--install";

    /// <summary>Removes them again (M8).</summary>
    public const string UninstallOption = "--uninstall";

    /// <summary>The §7 synopsis, quoted verbatim in every usage failure.</summary>
    public const string Usage = "FolderTreeMD.exe --folder \"<absolute path>\" [--to \"<output.md>\"] | --install | --uninstall";

    /// <summary>Kind of request this command line is.</summary>
    public CliCommandKind Kind { get; }

    /// <summary>Folder to list, or <c>null</c> unless <see cref="Kind"/> is <see cref="CliCommandKind.ListFolder"/>.</summary>
    public string? Folder { get; }

    /// <summary>Output file given with <c>--to</c>, or <c>null</c> when the clipboard default applies.</summary>
    public string? OutputPath { get; }

    /// <summary>Usage failure to report, or <c>null</c> unless <see cref="Kind"/> is <see cref="CliCommandKind.UsageError"/>.</summary>
    public string? Error { get; }

    private static readonly CliCommand WindowRequest = new(CliCommandKind.NotACliRequest, null, null, null);

    private CliCommand(CliCommandKind kind, string? folder, string? outputPath, string? error)
    {
        Kind = kind;
        Folder = folder;
        OutputPath = outputPath;
        Error = error;
    }

    /// <summary>
    /// Parses a command line against §7. Either option may appear in any order, unknown arguments are
    /// ignored, a repeated option keeps its last value, and an option token is never consumed as another
    /// option's value — so <c>--folder --to out.md</c> is a usage failure rather than a folder named
    /// <c>--to</c>. A known option written in the single-token <c>--name=value</c> form is a usage
    /// failure too: §7 writes the space-separated form, and ignoring the token instead would drop a
    /// delivery the caller asked for. A <c>null</c> element outside a value position is an unknown
    /// argument rather than an error: the scan stays tolerant of an array being sloppier than its type
    /// promises, as it was before the <c>=</c> rule was added. A malformed request never yields a
    /// command: it yields <see cref="CliCommandKind.UsageError"/>, which the caller must report instead
    /// of running.
    /// </summary>
    /// <param name="args">
    /// Raw startup arguments; a <c>null</c> element outside a value position is ignored. A <c>null</c> in
    /// a value position is a missing value, so the option it follows fails as if it had none.
    /// </param>
    /// <returns>The parsed command; never <c>null</c>.</returns>
    public static CliCommand Parse(string?[] args)
    {
        bool requested = false;
        bool install = false;
        bool uninstall = false;
        string? folder = null;
        string? outputPath = null;

        for (int index = 0; index < args.Length; index++)
        {
            // Checked before the plain names, so `--to=C:\out.md` can never be mistaken for an unknown
            // argument and silently dropped (which would deliver to the clipboard), and `--folder=…`
            // can never fall through to the window request — OCR fix round, D23.
            if (IsEqualsForm(args[index], FolderOption))
            {
                return UsageFailure($"{FolderOption} does not accept an '=' value; write {FolderOption} \"<absolute path>\"");
            }

            if (IsEqualsForm(args[index], OutputOption))
            {
                return UsageFailure($"{OutputOption} does not accept an '=' value; write {OutputOption} \"<output.md>\"");
            }

            if (IsEqualsForm(args[index], InstallOption) || IsEqualsForm(args[index], UninstallOption))
            {
                return UsageFailure($"{InstallOption} and {UninstallOption} carry no value");
            }

            if (IsOption(args[index], FolderOption))
            {
                requested = true;
                if (!TryTakeValue(args, ref index, out string value))
                {
                    return UsageFailure("--folder needs a folder path");
                }

                folder = value;
            }
            else if (IsOption(args[index], OutputOption))
            {
                requested = true;
                if (!TryTakeValue(args, ref index, out string value))
                {
                    return UsageFailure("--to needs a file path");
                }

                outputPath = value;
            }
            else if (IsOption(args[index], InstallOption))
            {
                requested = true;
                install = true;
            }
            else if (IsOption(args[index], UninstallOption))
            {
                requested = true;
                uninstall = true;
            }
        }

        if (!requested)
        {
            return WindowRequest;
        }

        // The two registration flags are whole commands, not modifiers: combining them with each other or
        // with a listing request is a usage failure rather than a silent precedence rule (D28).
        if (install || uninstall)
        {
            string flag = install ? InstallOption : UninstallOption;
            if (install && uninstall)
            {
                return UsageFailure($"{InstallOption} and {UninstallOption} cannot be combined");
            }

            if (folder is not null || outputPath is not null)
            {
                return UsageFailure($"{flag} cannot be combined with {FolderOption} or {OutputOption}");
            }

            return new CliCommand(install ? CliCommandKind.InstallShell : CliCommandKind.UninstallShell, null, null, null);
        }

        // An output path is only ever null because --to was never given: a --to without a usable value,
        // and a --to=… token, have already failed above, so the clipboard default can never stand in for
        // a requested file.
        return folder is null
            ? UsageFailure("--folder is required")
            : new CliCommand(CliCommandKind.ListFolder, folder, outputPath, null);
    }

    /// <summary>Compares one argument with an option name, ignoring case.</summary>
    /// <param name="argument">Raw argument, possibly <c>null</c> if a caller's array holds one.</param>
    /// <param name="option">Option name, including its leading dashes.</param>
    /// <returns>Whether the argument is that option.</returns>
    private static bool IsOption(string? argument, string option) =>
        string.Equals(argument, option, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an argument is exactly a known option name followed by <c>=</c> — the single-token form
    /// §7 does not use. The name must match in full (case-insensitively), so <c>--other=value</c> and
    /// <c>--folderx=1</c> stay ordinary unknown arguments. A <c>null</c> element is not the
    /// <c>=</c> form either, so the scan keeps the tolerance it always had.
    /// </summary>
    /// <param name="argument">Raw argument, possibly <c>null</c> if a caller's array holds one.</param>
    /// <param name="option">Option name, including its leading dashes.</param>
    /// <returns>Whether the argument is that option in the <c>--name=value</c> shape.</returns>
    private static bool IsEqualsForm(string? argument, string option) =>
        argument is not null
        && argument.Length > option.Length
        && argument[option.Length] == '='
        && argument.StartsWith(option, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Takes the value that follows an option, advancing <paramref name="index"/> past it. A missing,
    /// blank, option-like or <c>null</c> token is refused, so no option can swallow the next option and
    /// no caller's <c>null</c> can throw.
    /// </summary>
    /// <param name="args">Raw startup arguments.</param>
    /// <param name="index">Index of the option; advanced to the value when one is taken.</param>
    /// <param name="value">The value, when one was taken.</param>
    /// <returns>Whether a usable value followed.</returns>
    private static bool TryTakeValue(string?[] args, ref int index, out string value)
    {
        value = string.Empty;

        if (index + 1 >= args.Length)
        {
            return false;
        }

        string? candidate = args[index + 1];
        if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        index++;
        value = candidate;
        return true;
    }

    /// <summary>Builds a usage failure whose message carries the §7 synopsis.</summary>
    /// <param name="reason">What is wrong with the invocation.</param>
    /// <returns>The failure as a command value.</returns>
    private static CliCommand UsageFailure(string reason) =>
        new(CliCommandKind.UsageError, null, null, $"{reason}. Usage: {Usage}");
}
