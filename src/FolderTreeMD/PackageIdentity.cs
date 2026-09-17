namespace FolderTreeMD;

/// <summary>
/// Reads this process's package identity — the one fact the portable self-registration needs, and nothing more:
/// the decision itself lives in <c>ShellRegistration.ShouldSkipRegistration</c> (Core, tested).
/// </summary>
// LEARN[38] (M8 fix round 2, revised in round 3 for L34/L35): package identity decides whether the portable
// self-registration may run, and the *name* is what gets compared — not "has any identity".
// Why it was introduced: the packaged verb in the Windows 11 menu is served by the package's COM server, whose
//   Invoke starts `FolderTreeMD.exe --folder <path>` — the *listing* shape, i.e. the same code path a portable
//   run takes — so shape cannot tell them apart and the executable has to ask about its identity (L31).
// Why the name and not "Package.Current succeeds": identity is inherited, so "succeeds" is also true for a
//   portable copy started from a packaged host (a Store-build terminal, for instance), which would silently
//   stop registering (L34). Comparing `Package.Id.Name` to `ShellRegistration.PackagedIdentityName` answers
//   the question that matters, and the comparison is a pure Core function the suite can test (LEARN[39]).
// Why the failure direction is "register": a detection failure or a foreign identity means the portable case,
//   which is the human's explicit rule for this round (L35's report is answered by making the decision data
//   driven and tested rather than by classifying an exception).
// Alternatives considered: a `--no-register` CLI flag (rejected by the human: no new grammar, and a user could
//   pass it); `GetCurrentPackageFullName` P/Invoke (the same question with hand-written interop); guessing the
//   package from the exe's path (proves nothing — a sparse package's binaries live outside the package, D25);
//   the family name (embeds a publisher hash: re-creating the certificate would break the rule).
// See also: LEARN[36], LEARN[37], LEARN[39], D28, L31, L34, L35
internal static class PackageIdentity
{
    /// <summary>
    /// This process's package name (<c>Package.Id.Name</c>), or <c>null</c> when the process has no package
    /// identity — or when the identity could not be read, which the caller must treat the same way.
    /// </summary>
    internal static string? PackageName
    {
        get
        {
            try
            {
                return Windows.ApplicationModel.Package.Current.Id.Name;
            }
            catch (Exception)
            {
                // No package identity, or the identity could not be read: both mean "not this app's package",
                // which is the portable case, and the portable case registers.
                return null;
            }
        }
    }
}
