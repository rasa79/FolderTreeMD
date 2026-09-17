using Microsoft.Toolkit.Uwp.Notifications;

namespace FolderTreeMD;

/// <summary>
/// Shows the Windows toast that <c>UI_SPEC.md</c> §7 asks for in CLI mode, through the toolkit package
/// approved at M0 (Q1).
/// </summary>
// LEARN[25] (M6): the toast is delegated to Microsoft.Toolkit.Uwp.Notifications instead of being built
// on hand-rolled WinRT/COM interop.
// Context: an unpackaged Win32 process cannot post a toast at all — Windows requires an AppUserModelID
//   that is registered on the machine, which normally comes from an installed shortcut. What the
//   toolkit actually did here, verified after the first CLI run: it registered
//   HKCU\Software\Classes\AppUserModelId\<path of FolderTreeMD.exe> (DisplayName "FolderTreeMD", the
//   toolkit's own HasSentNotification flag, and a CustomActivator CLSID), stored an icon under
//   %LOCALAPPDATA%\ToastNotificationManagerCompat\Apps\<guid>\Icon.png, and then showed the
//   notification through the desktop toast APIs. No Start-menu shortcut appeared in this run.
// Alternatives considered: (a) hand-rolled COM interop against Windows.UI.Notifications plus a
//   self-made IShellLink shortcut — rejected: a few hundred lines of interop, a shell-link writer and
//   our own AUMID scheme, i.e. exactly the code this approved package already maintains; (b) toast only
//   when the app runs with MSIX package identity (M7) — rejected as the M6 default because §7 wants the
//   standalone CLI to be fully useful without M7; (c) a tray balloon or MessageBox — rejected: not a
//   toast, and a dialog contradicts "no window is shown in CLI mode".
// Pros: one PackageReference, no interop we own, and the same call works for the packaged app later.
// Cons: the package's dependency group is pulled in transitively (eight packages for this TFM), the
//   AUMID is derived from the executable path so moving the exe registers a second one, and the
//   toolkit's first-use registration is a persistent side effect on the machine — all recorded in
//   KNOWN_LIMITATIONS.md (L9).
// See also: LEARN[26]
//
// TODO[T1]: a click on the toast is not handled — the toolkit's activator starts the executable, which
// then shows the main window like any other launch. UI_SPEC §7 does not define toast activation, so
// activation handling is deferred rather than guessed (see KNOWN_LIMITATIONS.md, TODO index).
internal static class ToastNotifier
{
    /// <summary>
    /// Best-effort toast: shows <paramref name="message"/> when the process has an interactive session
    /// to show it in (§7), and never throws — the §7 exit code describes the listing, not the toast.
    /// </summary>
    /// <param name="message">Text to show in the notification body.</param>
    internal static void TryShow(string message)
    {
        if (!Environment.UserInteractive)
        {
            return; // no session to display it in; §7 wants the toast only when there is one
        }

        try
        {
            new ToastContentBuilder().AddText(message).Show();
        }
        catch (Exception exception)
        {
            // A failure here must not change the exit code, but it also must not be swallowed silently
            // (AGENTS.md: never claim a result without evidence).
            Console.Error.WriteLine($"FolderTreeMD: warning: the notification could not be shown: {exception.Message}");
            Console.Error.Flush();
        }
    }
}
