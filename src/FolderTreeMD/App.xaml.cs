using System.Windows;
using FolderTreeMD.Core;

namespace FolderTreeMD;

/// <summary>
/// Application entry point. Startup has four shapes, decided in order: the relaunched elevated long-paths
/// writer (writes one registry value and exits), Explorer's out-of-process COM server (no window, stays
/// resident), the <c>UI_SPEC.md</c> §7 command line (never shows a window), and the normal window — which
/// this class creates itself, because <c>App.xaml</c> deliberately has no <c>StartupUri</c>. M8 adds one
/// step that is not a shape of its own: the portable install's idempotent HKCU self-registration, which
/// runs for the CLI and window shapes and never for the COM server (D28, LEARN[36]).
/// </summary>
public partial class App : Application
{
    /// <summary>
    /// Dispatches the elevated long-paths helper, then the COM server, then §7 CLI mode, then the window.
    /// </summary>
    /// <param name="e">Startup arguments, as passed on the command line.</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains(LongPathsHelper.EnableArgument, StringComparer.OrdinalIgnoreCase))
        {
            // One-shot elevated helper: no window, no dialogs, exit code 0 on success. Exit terminates
            // before WPF reaches the rest of startup, so nothing is shown on screen.
            Environment.Exit(LongPathsHelper.TryEnableLongPaths() ? 0 : 2);
        }

        if (e.Args.Contains(ExplorerCommandInfo.EmbeddedArgument, StringComparer.OrdinalIgnoreCase))
        {
            // Explorer's out-of-process COM server (UI_SPEC §8, launched from the package manifest). No
            // window: the dispatcher keeps running so COM can deliver the shell's calls to this STA thread,
            // and the class object is the only thing published. A refused registration exits visibly
            // (code 2) instead of leaving a server that answers nothing.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (!ComServer.Register())
            {
                Environment.Exit(2);
            }

            base.OnStartup(e);
            return;
        }

        // The §7 grammar lives in Core as a pure function so the suite can test it (LEARN[30], D24); this
        // decides only what the process does with the result.
        CliCommand command = CliCommand.Parse(e.Args);
        if (command.Kind != CliCommandKind.NotACliRequest)
        {
            // §7: no window is shown in CLI mode, which is why the exit happens here rather than after the
            // normal startup below. The listing shape also runs M8's self-registration check inside the
            // runner (D28, LEARN[36]); --install/--uninstall are handled there too.
            Environment.Exit(CliRunner.Run(command));
        }

        // M8 (D28, LEARN[36]): the portable install keeps its own classic Explorer entries current. This is
        // the window shape, so the check runs here — idempotently, best-effort, and only under HKCU. The
        // -Embedded shape returned above and never reaches it: the packaged COM server must not write
        // classic-menu entries behind the MSIX registration's back.
        TryRegisterPortableEntry();

        base.OnStartup(e);

        // The normal shape: the window the user asked for. Created here rather than by StartupUri so the
        // CLI and COM shapes can start the same process without one (see App.xaml).
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    /// <summary>
    /// Runs M8's self-registration without letting any failure reach the caller: the entry is a convenience,
    /// the window is the app. Nothing is written when this process is part of this app's own package.
    /// </summary>
    // L26 (OCR M8 finding 2): the guard is deliberately a catch-all rather than the store's classified list,
    // because "best-effort, never stops the app" has to be literally true at the call site. A path the plan
    // refuses, a registry the user's policy locks down, or anything the .NET registry API throws on top of
    // those must not replace the window with WPF's unhandled-exception dialog. The failure is not silent
    // everywhere: `--install` returns exit code 2 and prints the reason, which is the path a user takes when
    // they actually want the entry (LEARN[36]).
    // L31/L36 (LEARN[38], LEARN[39]): the packaged case is the wrapper's business, not this call site's, so no
    // entry point can forget it — this one just runs the guarded path and swallows failures.
    private static void TryRegisterPortableEntry()
    {
        try
        {
            ShellRegistrationStore.EnsureRegisteredForPortableProcess();
        }
        catch (Exception)
        {
            // Deliberately swallowed: see the remark above.
        }
    }
}
