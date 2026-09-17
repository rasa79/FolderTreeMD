using System.IO;

namespace FolderTreeMD;

/// <summary>
/// Resolves the executable the app should point the shell at: the single rule shared by the §7 CLI launcher
/// (M7's <c>Invoke</c>) and M8's registry registration.
/// </summary>
// LEARN[37] (M8 fix round, closing L28): one resolver for "the app's own executable" instead of two rules.
// Why: M7's Invoke preferred the apphost beside the assembly and fell back to the running image, while M8's
//   registration preferred the running image — two answers to a question that looks like one, and the M8
//   order carried a real trap: run through `dotnet FolderTreeMD.dll` and the running image is `dotnet.exe`,
//   which must never be written into a shell command line (it would launch the host with no assembly and
//   list nothing). The rule here takes the running image when that is this app — the single-file portable
//   case, the apphost case and the MSIX server case — and otherwise the apphost beside the assembly, which
//   is exactly what `dotnet FolderTreeMD.dll` needs. A .NET host is never returned while an app-shaped
//   candidate exists.
// Alternatives considered: (a) keep both rules — rejected: the review found the inconsistency (OCR M8
//   finding 1) and the next reader would "fix" one into the other, breaking either the repair-after-move
//   contract or the MSIX invocation; (b) always BaseDirectory-first — rejected: rename the portable exe and
//   a stale `FolderTreeMD.exe` in the same folder would be registered instead of the image actually running;
//   (c) always ProcessPath-first without the host check — rejected: it writes `dotnet.exe --folder "%1"`
//   into the registry, an entry that can never work.
// Pros: one rule, one place to reason about it, and the dotnet-host trap is closed for both callers.
// Cons: the app project has no unit tests (D4, LEARN[1]), so this resolver is verified by inspection and by
//   the manual runs recorded in STATUS.md rather than by the suite.
// See also: LEARN[36], LEARN[26], D28
internal static class AppExecutable
{
    /// <summary>The file name the app is built and distributed as.</summary>
    internal const string FileName = "FolderTreeMD.exe";

    /// <summary>Host executables that run a managed assembly but are not this app.</summary>
    private static readonly string[] HostNames = ["dotnet", "dotnet.exe"];

    /// <summary>
    /// Returns the path the shell should run: the running image when that is this app, otherwise the apphost
    /// beside this assembly. Never a .NET host executable.
    /// </summary>
    /// <returns>An absolute path to this app's executable.</returns>
    internal static string Resolve()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, FileName);
        string? running = Environment.ProcessPath;

        if (!string.IsNullOrWhiteSpace(running) && !IsHost(running) && File.Exists(running))
        {
            return running;
        }

        return beside;
    }

    /// <summary>Whether a path names a .NET host rather than this app.</summary>
    /// <param name="path">Candidate executable path.</param>
    /// <returns>Whether it is a host executable.</returns>
    internal static bool IsHost(string path) =>
        HostNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
}
