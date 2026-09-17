using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using FolderTreeMD.Core;
using Microsoft.Win32;

namespace FolderTreeMD;

/// <summary>
/// Reads and writes the machine-wide long-path switch that <c>UI_SPEC.md</c> §3.3 exposes as the
/// SYSTEM panel button: <c>HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled</c>.
/// </summary>
/// <remarks>
/// Writing HKLM needs administrator rights, and a non-elevated process cannot become elevated in
/// place, so the write happens in a second copy of this executable: the button relaunches the app
/// with <c>runas</c> and <see cref="EnableArgument"/>, that instance writes the value and exits
/// immediately, and the original instance re-reads the value afterwards. That is one UAC prompt.
/// </remarks>
// LEARN[14]: elevation is handled by relaunching this executable with an argument instead of a
// separate helper binary or an in-process elevation shim.
// Alternatives considered: (a) a second small helper executable — rejected: another project (and the
//   plan fixes the project list) plus its own packaging for a one-registry-write job; (b) an in-process
//   elevation shim (e.g. COM elevation moniker / `runas` on the running assembly) — rejected: WPF
//   cannot elevate in place, and the shim approaches are harder to review than a documented relaunch;
//   (c) asking the user to edit the registry manually — rejected: §3.3 specifies a one-click button
//   with a single UAC prompt.
// Pros of chosen approach: reuses the application's own startup path, keeps the elevated code path
//   trivially small (write one DWORD and exit before any window exists), and needs no extra artifact.
// Cons of chosen approach: the elevated instance is the whole application binary, so the argument
//   handling in App.OnStartup must exit before WPF creates a window; and `Environment.ProcessPath`
//   is the apphost, so pressing the button under `dotnet run` would relaunch the host rather than the
//   app (see KNOWN_LIMITATIONS L7).
// See also: LEARN[12]
internal static class LongPathsHelper
{
    /// <summary>Command-line argument that turns a launched instance into the elevated writer.</summary>
    internal const string EnableArgument = "--enable-long-paths";

    private const string RegistryKeyPath = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string ValueName = "LongPathsEnabled";

    /// <summary>
    /// Whether the machine-wide long-path switch is already on.
    /// </summary>
    /// <returns><c>true</c> when <c>LongPathsEnabled</c> is 1; <c>false</c> when it is missing, unreadable or 0.</returns>
    public static bool IsLongPathsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath);
            return key?.GetValue(ValueName) is int value && value == 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <c>LongPathsEnabled = 1</c> for the whole machine. Intended to run in the elevated
    /// instance; a non-elevated call simply fails and reports <c>false</c>.
    /// </summary>
    /// <returns><c>true</c> when the value is 1 afterwards.</returns>
    public static bool TryEnableLongPaths()
    {
        if (IsLongPathsEnabled())
        {
            return true;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
            if (key is null)
            {
                return false;
            }

            key.SetValue(ValueName, 1, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Relaunches this executable elevated with <see cref="EnableArgument"/> and waits asynchronously
    /// for it, so the caller can re-read the registry once the write has happened without blocking the
    /// UI thread while the UAC prompt is up.
    /// </summary>
    /// <param name="timeout">How long to wait for the elevated instance.</param>
    /// <returns>
    /// <see cref="LongPathEnableOutcome.Succeeded"/> when the helper exited 0,
    /// <see cref="LongPathEnableOutcome.Declined"/> when the user dismissed the UAC prompt,
    /// <see cref="LongPathEnableOutcome.TimedOut"/> when it had to be terminated, and
    /// <see cref="LongPathEnableOutcome.Failed"/> for any other exit code or start failure.
    /// </returns>
    // LEARN[17] (M4 review items 3 and 5; revised in round 3): the elevated launch is offloaded and
    // awaited, and the wait result and the helper's exit code are both observed.
    // Alternatives considered: (a) the first M4 version — a blocking WaitForExit whose result was
    //   discarded — rejected on two counts: it froze the dispatcher for up to the whole timeout and it
    //   reported success for a helper that timed out or failed; (b) the second M4 version — an `async`
    //   method that still called Process.Start directly — rejected by review: the UAC prompt is answered
    //   inside ShellExecute, so the launch itself blocks the dispatcher until the user answers, and an
    //   `async` signature before the first await changes nothing; (c) a blocking wait on a worker
    //   thread — rejected: it moves the block rather than removing it; (d) no timeout — rejected: a
    //   wedged helper would leave the button disabled forever.
    // Pros of chosen approach: the dispatcher is free from the moment the click is handled (only the
    //   launch and the wait happen off-thread), the outcome is the process's real result, and the caller
    //   has one value to map onto the button state (LongPathStatus.Describe).
    // Cons of chosen approach: the click handler is `async void` (the WPF idiom), so the button must be
    //   disabled for the duration to prevent a second prompt — which the handler does.
    // See also: LEARN[14], LEARN[16], LEARN[19]
    /// <summary>
    /// Relaunches this executable elevated and reports how the attempt ended.
    /// </summary>
    /// <param name="timeout">How long to wait for the elevated instance.</param>
    /// <returns>The outcome, including the helper's exit code.</returns>
    public static async Task<LongPathEnableOutcome> RelaunchElevatedAsync(TimeSpan timeout)
    {
        string? executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
        {
            return LongPathEnableOutcome.Failed;
        }

        Process? process;
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = EnableArgument,
            };

            // The UAC prompt is answered *inside* ShellExecute, which Process.Start performs
            // synchronously — being in an async method does not help until the first await. Offloading
            // the launch is what actually keeps the dispatcher painting while the prompt is up
            // (M4 round-3 item 1, LEARN[17]).
            process = await Task.Run(() => Process.Start(startInfo)).ConfigureAwait(true);
        }
        catch (Win32Exception exception)
        {
            // Only ERROR_CANCELLED means the user dismissed the prompt; every other start failure is a
            // real failure and must be reported as one (M4 round-3 item 2, LEARN[19]).
            return LongPathStatus.ClassifyStartFailure(exception.NativeErrorCode);
        }
        catch (InvalidOperationException)
        {
            return LongPathEnableOutcome.Failed;
        }

        if (process is null)
        {
            return LongPathEnableOutcome.Failed;
        }

        using (process)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(cancellation.Token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);

                // The helper never reported back, so the registry may or may not have been written;
                // a timeout must not be presented as success.
                return LongPathEnableOutcome.TimedOut;
            }

            // App.OnStartup exits 0 when the value is set and 2 when it is not (see App.xaml.cs).
            return process.ExitCode == 0 ? LongPathEnableOutcome.Succeeded : LongPathEnableOutcome.Failed;
        }
    }

    /// <summary>
    /// Terminates a helper that outlived its timeout, ignoring races with its own exit.
    /// </summary>
    /// <param name="process">The process to kill.</param>
    /// <remarks>
    /// <see cref="Process.Kill(bool)"/> documents <see cref="AggregateException"/> for the case where
    /// part of the process tree could not be killed, so that failure mode must be caught here too
    /// (M4 round-3 item 3) — otherwise a partial kill would escape as an unhandled exception instead of
    /// the timeout outcome the caller is about to return.
    /// </remarks>
    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already gone, or not (fully) killable — the outcome is TimedOut either way.
        }
    }
}
