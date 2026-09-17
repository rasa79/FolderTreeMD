using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using FolderTreeMD.Core;

namespace FolderTreeMD;

/// <summary>
/// The <c>UI_SPEC.md</c> §7 command-line contract: turn one folder into markdown and deliver it to the
/// clipboard (default) or to a file (<c>--to</c>), then report the outcome as one of §7's exit codes.
/// </summary>
// LEARN[26] (M6): the CLI is a thin sequential runner in the app project, not a new project and not a
// service, and it is entered before WPF ever creates a window.
// Alternatives considered: (a) a separate console project (FolderTreeMD.Cli) — rejected: §7 fixes the
//   executable as FolderTreeMD.exe, and M7 has to invoke that same image from the Explorer verb, so a
//   second project would only add a place for the two entry points to drift apart; (b) a
//   "--cli" console build with OutputType Exe — rejected: the same image must still be the GUI app, and
//   Windows decides the console subsystem at build time; (c) a Windows service or named-pipe listener —
//   rejected: enormous machinery for a one-shot command whose whole lifetime is one folder walk.
// Pros: one executable, one settings file, one engine; the CLI and the window share SettingsStore,
//   SettingsMapping and the engine, so the §7 requirement "saved settings are honoured" cannot diverge.
// Cons: the process is a WinExe, so stdout/stderr only reach a console that handed the handles down
//   (which is the case when it is launched from a terminal or by Explorer); this is recorded in
//   KNOWN_LIMITATIONS.md (L9) rather than worked around with a console attach.
// See also: LEARN[25], LEARN[27], LEARN[30]
internal static class CliRunner
{
    /// <summary>§7 exit code: the listing was produced and delivered.</summary>
    internal const int SuccessExitCode = 0;

    /// <summary>§7 exit code: the invocation was malformed, or the given folder does not exist.</summary>
    internal const int FolderNotFoundExitCode = 1;

    /// <summary>§7 exit code: enumeration or delivery failed.</summary>
    internal const int FailedExitCode = 2;

    /// <summary>How often, and how long, the clipboard is retried before the run fails (LEARN[27]).</summary>
    private const int ClipboardAttempts = 5;

    /// <summary>Delay between two clipboard attempts, in milliseconds.</summary>
    private const int ClipboardRetryDelayMilliseconds = 100;

    /// <summary>
    /// Runs one §7 command and returns its exit code. Never throws: the process is a WinExe, so an
    /// escaping exception would surface as WPF's unhandled-exception dialog — a window, in a mode whose
    /// contract is that no window is shown.
    /// </summary>
    /// <param name="command">The parsed command line (<see cref="CliCommand.Parse"/>).</param>
    /// <returns>The §7 exit code.</returns>
    internal static int Run(CliCommand command)
    {
        try
        {
            return RunCore(command);
        }
        catch (Exception exception)
        {
            return Fail($"the listing could not be produced: {exception.Message}", FailedExitCode);
        }
    }

    /// <summary>The §7 sequence: report a malformed command, check the folder, generate, then deliver.</summary>
    /// <param name="command">The parsed command line.</param>
    /// <returns>The §7 exit code.</returns>
    private static int RunCore(CliCommand command)
    {
        // M8's two registration commands are whole commands of their own (D28, LEARN[36]): they do the
        // registry work and report, and never fall through to a listing.
        if (command.Kind == CliCommandKind.InstallShell)
        {
            return ShellRegistrationStore.EnsureRegistered()
                ? SuccessExitCode
                : Fail("the Explorer entry could not be registered in HKEY_CURRENT_USER", FailedExitCode);
        }

        if (command.Kind == CliCommandKind.UninstallShell)
        {
            return ShellRegistrationStore.Uninstall()
                ? SuccessExitCode
                : Fail("the Explorer entry could not be removed from HKEY_CURRENT_USER", FailedExitCode);
        }

        string? folder = command.Folder;

        if (command.Kind != CliCommandKind.ListFolder || string.IsNullOrWhiteSpace(folder))
        {
            // §7: every malformed invocation is one usage line on stderr and exit code 1 — it never falls
            // back to the clipboard sink, because the parser makes a valueless or "--to=…" form a usage
            // failure rather than a missing --to (D23, D24). The guard is deliberately local instead of
            // trusting the parser's invariant across files: `Run` is internal, and a second caller
            // handing it a window request would otherwise turn a null folder into a swallowed
            // NullReferenceException reported as exit code 2.
            return Fail(
                command.Error ?? $"--folder is required. Usage: {CliCommand.Usage}",
                FolderNotFoundExitCode);
        }

        // The startup self-registration check (D28, LEARN[36]) runs for this shape too: a click on the
        // portable Explorer entry starts the process here, so this is the moment a moved exe repairs its
        // registry entry. It goes through the one guarded wrapper (LEARN[39], L36), which registers unless this
        // process is part of this app's own package — the packaged verb reaches this same listing shape from
        // ExplorerCommand.Invoke, and an installed package must not write the portable classic-menu keys (L31).
        // Best-effort on purpose: a registry the user's policy locks down must not stop a listing.
        ShellRegistrationStore.EnsureRegisteredForPortableProcess();

        if (!Directory.Exists(folder))
        {
            return Fail($"the folder does not exist: {folder}", FolderNotFoundExitCode);
        }

        string markdown;
        try
        {
            // §7: the saved settings are honoured — the same file and the same loader the window uses.
            AppSettings settings = SettingsStore.Load();
            markdown = MarkdownListing.Generate(
                folder,
                SettingsMapping.ToOptions(settings),
                progress: null,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            // D5: the engine throws when the root cannot be read; every other enumeration failure lands
            // here too, and §7 calls all of them exit code 2.
            return Fail($"the folder could not be listed: {exception.Message}", FailedExitCode);
        }

        if (command.OutputPath is { } outputPath)
        {
            try
            {
                // §5 rule 12's encoding, through the same Core call the window's Save uses (LEARN[21]).
                ListingFile.Save(outputPath, markdown);
            }
            catch (Exception exception)
            {
                return Fail($"the output file could not be written: {exception.Message}", FailedExitCode);
            }

            return SuccessExitCode;
        }

        try
        {
            CopyToClipboard(markdown);
        }
        catch (ExternalException exception)
        {
            return Fail($"the markdown could not be copied to the clipboard: {exception.Message}", FailedExitCode);
        }

        ToastNotifier.TryShow("FolderTreeMD — markdown copied to clipboard");
        return SuccessExitCode;
    }

    /// <summary>
    /// Copies the listing to the clipboard, tolerating a transiently locked clipboard.
    /// </summary>
    /// <param name="markdown">Listing text to place on the clipboard.</param>
    /// <exception cref="ExternalException">The clipboard stayed unavailable for every attempt.</exception>
    // LEARN[27] (M6): the clipboard copy is written with copy:true and retried, because the CLI process
    // exits immediately after it.
    // Alternatives considered: (a) Clipboard.SetText — rejected: WPF places that data with copy:false,
    //   i.e. as delayed rendering owned by this process, and the content can be lost the moment the
    //   process exits, which is exactly what a one-shot CLI does; (b) no retry — rejected: another
    //   process holding the clipboard open for a moment is normal (CLIPBRD_E_CANT_OPEN) and would turn
    //   a working command into exit code 2; (c) retry forever — rejected: the Explorer verb (M7) runs
    //   unattended, so the command has to finish and report; (d) an STA worker thread — rejected: WPF's
    //   Clipboard requires an STA thread, and OnStartup already runs on the STA UI thread.
    // Pros: the content survives the process exit (verified by reading it back from a later process) and
    //   a briefly locked clipboard is absorbed.
    // Cons: up to 400 ms of extra latency in the worst case, and the data is copied rather than rendered
    //   on demand, which is the point.
    // See also: LEARN[26]
    private static void CopyToClipboard(string markdown)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(markdown, copy: true);
                return;
            }
            catch (ExternalException) when (attempt < ClipboardAttempts)
            {
                Thread.Sleep(ClipboardRetryDelayMilliseconds);
            }
        }
    }

    /// <summary>
    /// Reports a failure the §7 way — one line on stderr, plus a toast when there is a session for it —
    /// and returns the exit code for it.
    /// </summary>
    /// <param name="message">What went wrong, without the program prefix.</param>
    /// <param name="exitCode">The §7 exit code for this failure.</param>
    /// <returns><paramref name="exitCode"/>.</returns>
    private static int Fail(string message, int exitCode)
    {
        Console.Error.WriteLine($"FolderTreeMD: {message}");
        Console.Error.Flush();
        ToastNotifier.TryShow($"FolderTreeMD — {message}");
        return exitCode;
    }
}
