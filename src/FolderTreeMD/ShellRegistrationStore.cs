using System.IO;
using System.Security;
using FolderTreeMD.Core;
using Microsoft.Win32;

namespace FolderTreeMD;

/// <summary>
/// Applies <see cref="ShellRegistration"/>'s plan to <c>HKEY_CURRENT_USER</c>: the thin side that touches the
/// registry, while every path, string and comparison stays in the tested plan (LEARN[35]).
/// </summary>
// LEARN[36] (M8, D28): the portable install writes its Explorer entries under HKCU on every startup, and
// never for the packaged COM server.
// Why: a portable exe can be copied anywhere and run from there, so the only thing that can keep the entry
//   pointing at the executable the user is actually running is the executable itself. Doing it on every
//   startup is what makes it self-repairing: created when absent, rewritten when the exe moved (the case a
//   one-shot installer cannot handle), and — via the plan's IsUpToDate comparison — nothing is written at
//   all when the registry already matches, so an unchanged install causes no writes and no registry churn.
// Alternatives considered: (a) only on --install — rejected: the entry would silently rot the first time the
//   exe is moved, which is the normal fate of a portable file; (b) at MSIX startup too — rejected: the
//   packaged path already registers the modern-menu verb through the manifest (M7), and the MSIX COM server
//   must not start writing classic HKCU entries behind the package's back; hence the -Embedded skip;
//   (c) a scheduled task or a service — rejected: enormous machinery for three registry values;
//   (d) HKLM — rejected: needs elevation, and D28 says HKCU only; (e) failing loudly when the registry is
//   unwritable — rejected: the window and the CLI are the app's real work, and a locked-down registry must
//   not stop a listing; the failure is reported only when the user asked for --install explicitly.
// Pros: the portable copy is self-contained and self-repairing, nothing runs elevated, and "run it once"
//   is literally all the user has to do.
// Cons: the app touches the user's registry without being asked (documented in the README and in
//   KNOWN_LIMITATIONS.md L24/L25); a locked HKCU silently leaves the entry absent until the user runs
//   --install and sees the failure.
// See also: LEARN[35], D28.
internal static class ShellRegistrationStore
{
    /// <summary>
    /// Whether the registry already holds exactly the plan for this executable. Reads only; never throws —
    /// an unreadable hive reads as "not registered", which the caller then tries to repair.
    /// </summary>
    /// <returns>Whether every planned value is present and identical.</returns>
    internal static bool IsUpToDate()
    {
        try
        {
            return Plan().IsUpToDate(ReadValue);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the entries when they are missing and repairs them when they point somewhere else — the
    /// idempotent startup check: when the plan already holds, nothing is written.
    /// </summary>
    /// <returns>Whether the registry holds the plan afterwards.</returns>
    internal static bool EnsureRegistered()
    {
        try
        {
            ShellRegistrationPlan plan = Plan();
            foreach (ShellRegistryValue value in plan.Values)
            {
                if (!string.Equals(ReadValue(value.KeyPath, value.ValueName), value.Value, StringComparison.Ordinal))
                {
                    WriteValue(value);
                }
            }

            return plan.IsUpToDate(ReadValue);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>
    /// The one way in for every automatic call site: registers the portable entries, unless this process runs
    /// as part of this app's own package.
    /// </summary>
    /// <returns>
    /// Whether the registry holds the plan afterwards, or the registration was skipped because the process is
    /// packaged (<c>true</c> either way); <c>false</c> when the plan could not be applied.
    /// </returns>
    // LEARN[39] (M8 fix round 3, closing L36): the guard lives in one wrapper instead of at each call site.
    // Why: L31 was a new entry point (the packaged verb's CLI child) reaching `EnsureRegistered()` with no guard
    //   at all, and only a re-review caught it; an `if` copied to each caller keeps that possible, a wrapper does
    //   not. `EnsureRegistered()` stays public-to-the-assembly for the explicit `--install` request, which is
    //   the user asking for the entry and therefore not subject to the skip.
    // Alternatives considered: the guard repeated at each call site (what the previous round did, and what L36
    //   records as the fragile shape); putting the guard inside `EnsureRegistered()` itself (rejected: it would
    //   make `--install` silently do nothing inside the package, where the user asked for it explicitly).
    // See also: LEARN[36], LEARN[38], D28, L31, L36
    internal static bool EnsureRegisteredForPortableProcess()
    {
        if (ShellRegistration.ShouldSkipRegistration(PackageIdentity.PackageName))
        {
            return true;
        }

        return EnsureRegistered();
    }

    /// <summary>Removes both verb keys, tolerating the case where they were never written.</summary>
    /// <returns>Whether neither key exists afterwards.</returns>
    internal static bool Uninstall()
    {
        try
        {
            foreach (string keyPath in Plan().Keys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
            }

            return Plan().Keys.All(keyPath => Registry.CurrentUser.OpenSubKey(keyPath) is null);
        }
        catch (Exception exception) when (IsRegistryFailure(exception))
        {
            return false;
        }
    }

    /// <summary>The executable this process is running as — the path the entries must point at.</summary>
    /// <returns>An absolute path to the running image.</returns>
    // One rule for the CLI launcher and this registration (LEARN[37], L28): the app's own image, never a .NET
    // host, and the apphost beside the assembly when the running image is a host.
    private static string ExecutablePath() => AppExecutable.Resolve();

    /// <summary>Builds the plan for the running executable.</summary>
    /// <returns>The plan to apply.</returns>
    private static ShellRegistrationPlan Plan() => ShellRegistration.Plan(ExecutablePath());

    /// <summary>Reads one registry value, or <c>null</c> when the key or the value is absent.</summary>
    /// <param name="keyPath">Key path below <c>HKEY_CURRENT_USER</c>.</param>
    /// <param name="valueName">Value name, or <c>null</c> for the key's default value.</param>
    /// <returns>The current value, or <c>null</c>.</returns>
    private static string? ReadValue(string keyPath, string? valueName)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    /// <summary>Writes one planned value, creating the key when it does not exist yet.</summary>
    /// <param name="value">The value to write.</param>
    private static void WriteValue(ShellRegistryValue value)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(value.KeyPath, writable: true)
            ?? throw new IOException($"the registry key could not be created: {value.KeyPath}");
        key.SetValue(value.ValueName, value.Value, RegistryValueKind.String);
    }

    /// <summary>The failures a registry write can produce, all of which mean "the entry is not there".</summary>
    /// <param name="exception">The exception to classify.</param>
    /// <returns>Whether it is a registry failure the caller should report as "not registered".</returns>
    // The plan's own validation failures are included on purpose (L26, OCR M8 finding 2): Plan throws
    // ArgumentException for a path it refuses, and an unclassified type would otherwise escape a call the
    // contract calls best-effort.
    private static bool IsRegistryFailure(Exception exception) =>
        exception is SecurityException or UnauthorizedAccessException or IOException or InvalidOperationException or ObjectDisposedException
            or ArgumentException or ArgumentOutOfRangeException;
}
