using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FolderTreeMD.Core;

namespace FolderTreeMD;

/// <summary>
/// Identifiers shared by the COM class, the out-of-process server and <c>AppxManifest.xml</c>. The CLSID
/// here and the <c>com:Class/@Id</c> in the package manifest must always match: nothing at runtime checks
/// that for us, and a mismatch shows up only as a missing context-menu entry.
/// </summary>
internal static class ExplorerCommandInfo
{
    /// <summary>The command's CLSID, as a string for the <see cref="GuidAttribute"/>.</summary>
    internal const string ClsidString = "E3B2A79E-EF53-4032-8C6D-8B99CA2BE14C";

    /// <summary>The same CLSID as a <see cref="Guid"/>.</summary>
    internal static readonly Guid Clsid = new(ClsidString);

    /// <summary>Label shown in the Windows 11 context menu (UI_SPEC §8).</summary>
    internal const string Title = "Copy folder content as markdown";

    /// <summary>Tooltip shown for the entry.</summary>
    internal const string ToolTip = "Writes this folder's content as a markdown list and copies it to the clipboard";

    /// <summary>Argument that starts the process as Explorer's COM server instead of as a UI or CLI run.</summary>
    internal const string EmbeddedArgument = "-Embedded";
}

/// <summary>
/// The <c>IExplorerCommand</c> host behind <c>UI_SPEC.md</c> §8: the Windows 11 context-menu entry for a
/// folder. <see cref="Invoke"/> runs the M6 §7 CLI contract in a fresh process, so the markdown, the
/// toast and the exit codes are exactly what a terminal invocation produces.
/// </summary>
/// <remarks>
/// The class is instantiated by <see cref="ComServer"/>'s class factory, which Explorer reaches through
/// the sparse package's <c>windows.comServer</c> registration; the app writes no registry keys. The shell
/// calls these methods while it builds the menu, so they stay cheap — the work happens in the process
/// <see cref="Invoke"/> starts.
/// </remarks>
// LEARN[32] (M7): the context-menu command is an out-of-process COM server written in C#, hosted by the
// app's own executable and registered only through the sparse package manifest.
// Alternatives considered: (a) a native C++ DLL with a SurrogateServer, which is what the Microsoft
//   documentation shows — rejected: it would add a C++ toolchain and a second binary to the project for
//   one method call, and this repository has no C++ build at all; (b) an in-process C# DLL (DllServer) —
//   rejected: a managed assembly cannot implement DllGetClassObject for loading into explorer.exe, and
//   hosting a runtime inside the shell process is hostile to the shell; (c) a classic registry-registered
//   shell extension (HKCR\*\shellex) — rejected: UI_SPEC §8 and PLAN.md forbid the registry fallback;
//   (d) doing the work inside Invoke instead of starting the CLI — rejected: the shell's COM server
//   process is not the app, so the toast, the clipboard retry and the exit-code contract would all have
//   to be reimplemented here.
// Pros of chosen approach: one executable hosts both the server and the CLI; the package manifest is the
//   only registration; Invoke is a one-line process start that reuses the tested §7 path.
// Cons: the server process stays resident while the shell holds the class object (no idle shutdown), and
//   the route can only be verified end to end by a real right-click (the human checklist), so the machine
//   checks cover the manifest, the COM activation and the CLI the verb starts.
// See also: LEARN[26], LEARN[33]
[ComVisible(true)]
[Guid(ExplorerCommandInfo.ClsidString)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class ExplorerCommand : IExplorerCommand, IObjectWithSite
{
    /// <summary>
    /// The site the shell hands us, if it calls <see cref="SetSite"/>. Kept because the
    /// <c>Directory\Background</c> surface can arrive with an empty item array — there is no selection to
    /// derive the folder from — and the site is then the only other route to the folder being viewed.
    /// </summary>
    private object? _site;

    private static int _instanceCounter;

    /// <summary>
    /// Identifies this instance in the log. The shell may create one instance per call, so a line that
    /// shows the site arriving on a different instance than the one being invoked explains a failure
    /// without another round of guessing.
    /// </summary>
    private readonly int _instanceId = Interlocked.Increment(ref _instanceCounter);

    /// <summary>Supplies the context-menu label.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="title">Label to show.</param>
    /// <returns>An HRESULT.</returns>
    public int GetTitle(IShellItemArray? itemArray, out string? title)
    {
        title = ExplorerCommandInfo.Title;
        EmbeddedLog.Write($"GetTitle -> 0x{HResult.Ok:X8} '{title}'");
        return HResult.Ok;
    }

    /// <summary>Supplies the entry's icon reference.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="icon">Icon reference, as "path,index".</param>
    /// <returns>An HRESULT.</returns>
    public int GetIcon(IShellItemArray? itemArray, out string? icon)
    {
        // The app's own icon group: ApplicationIcon in the csproj is the only icon resource it has.
        icon = Path.Combine(AppContext.BaseDirectory, "FolderTreeMD.exe") + ",0";
        EmbeddedLog.Write($"GetIcon -> 0x{HResult.Ok:X8} '{icon}'");
        return HResult.Ok;
    }

    /// <summary>Supplies the entry's tooltip.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="toolTip">Tooltip to show.</param>
    /// <returns>An HRESULT.</returns>
    public int GetToolTip(IShellItemArray? itemArray, out string? toolTip)
    {
        toolTip = ExplorerCommandInfo.ToolTip;
        EmbeddedLog.Write($"GetToolTip -> 0x{HResult.Ok:X8} '{toolTip}'");
        return HResult.Ok;
    }

    /// <summary>Reports no canonical name — the shell uses the CLSID when this is empty.</summary>
    /// <param name="canonicalName">Canonical name to report.</param>
    /// <returns>An HRESULT.</returns>
    public int GetCanonicalName(out Guid canonicalName)
    {
        canonicalName = Guid.Empty;
        EmbeddedLog.Write($"GetCanonicalName -> 0x{HResult.Ok:X8} (empty guid)");
        return HResult.Ok;
    }

    /// <summary>Always enables the entry: the manifest scopes the verb to folders and folder background.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="okToBeSlow">Whether the shell tolerates a slow answer.</param>
    /// <param name="state">State to show.</param>
    /// <returns>An HRESULT.</returns>
    public int GetState(IShellItemArray? itemArray, bool okToBeSlow, out EXPCMDSTATE state)
    {
        // §8 registers the verb on Directory and Directory\Background only, so every call this command
        // receives is about something listable and the entry is always enabled. On the background surface
        // the shell hands over an empty (or absent) selection — there is no selected item at all — and
        // hiding the entry there would remove it from exactly the surface §8 asks for, so the count is
        // logged for evidence and never used to hide the entry. Invoke then resolves the folder itself.
        int itemCount = 0;
        bool counted = itemArray is not null && itemArray.GetCount(out itemCount) == HResult.Ok;

        state = EXPCMDSTATE.Enabled;
        // "background surface" only when the shell really said "no items" — not when it handed over nothing to
        // count, which is a different fact and would otherwise be logged as if it were the background
        // (OCR round-4 finding 3).
        string surface = counted && itemCount == 0 ? ", background surface" : string.Empty;
        EmbeddedLog.Write($"GetState -> 0x{HResult.Ok:X8} {state} (instance {_instanceId}, selection items: {(counted ? itemCount.ToString() : "unavailable")}{surface})");
        return HResult.Ok;
    }

    /// <summary>Runs the §7 CLI contract for the selected folder, or for the folder being viewed.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="bindContext">Bind context; unused.</param>
    /// <returns>An HRESULT.</returns>
    public int Invoke(IShellItemArray? itemArray, IntPtr bindContext)
    {
        string folder;
        string source;
        if (TryGetSelectedFolder(itemArray, out folder))
        {
            source = "selection";
        }
        else if (TryGetFolderFromSite(out folder))
        {
            // The background surface arrives without a selection (§8 registers a Directory\Background
            // verb), so the folder has to come from the site the shell handed to SetSite.
            source = "site";
        }
        else
        {
            EmbeddedLog.Write($"Invoke -> 0x{HResult.Fail:X8} (no folder: the selection had none and the site gave none)");
            return HResult.Fail;
        }

        try
        {
            // §8: invoke the M6 CLI contract, which copies the listing and shows the toast. No window is
            // shown, and the shell's server process stays out of the work. The executable is the app's own
            // image, resolved by the one rule the M8 registration shares (AppExecutable, LEARN[37], L28) —
            // which is also what keeps a `dotnet FolderTreeMD.dll` host from ever being run as the CLI.
            string executable = AppExecutable.Resolve();

            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(CliCommand.FolderOption);
            startInfo.ArgumentList.Add(folder);

            using Process? process = Process.Start(startInfo);
            EmbeddedLog.Write($"Invoke('{folder}') -> 0x{(process is null ? HResult.Fail : HResult.Ok):X8} (folder from {source}; started pid {(process is null ? "none" : process.Id.ToString())} from '{executable}')");
            return process is null ? HResult.Fail : HResult.Ok;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            EmbeddedLog.Write($"Invoke('{folder}') -> 0x{HResult.Fail:X8} ({exception.GetType().Name}: {exception.Message})");
            return HResult.Fail;
        }
    }

    /// <summary>Accepts the site the shell offers, which is the only route to the viewed folder on the background surface.</summary>
    /// <param name="site">The site object, or <c>null</c> to clear it.</param>
    /// <returns>An HRESULT.</returns>
    public int SetSite(object? site)
    {
        _site = site;
        EmbeddedLog.Write($"SetSite(instance {_instanceId}) -> 0x{HResult.Ok:X8} ({DescribeSite(site)})");
        return HResult.Ok;
    }

    /// <summary>Returns the site previously accepted, for the requested interface.</summary>
    /// <param name="interfaceId">Interface the caller asks for.</param>
    /// <param name="site">Receives the interface pointer, or zero.</param>
    /// <returns>An HRESULT.</returns>
    public int GetSite(ref Guid interfaceId, out IntPtr site)
    {
        site = IntPtr.Zero;
        if (_site is null)
        {
            return HResult.Fail;
        }

        IntPtr unknown = Marshal.GetIUnknownForObject(_site);
        try
        {
            Guid requested = interfaceId;
            return Marshal.QueryInterface(unknown, ref requested, out site);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Reports default flags: a single command with no subcommands.</summary>
    /// <param name="flags">Flags to report.</param>
    /// <returns>An HRESULT.</returns>
    public int GetFlags(out EXPCMDFLAGS flags)
    {
        flags = EXPCMDFLAGS.Default;
        EmbeddedLog.Write($"GetFlags -> 0x{HResult.Ok:X8} {flags}");
        return HResult.Ok;
    }

    /// <summary>Reports no subcommands.</summary>
    /// <param name="enumSubCommands">Unused.</param>
    /// <returns><c>E_NOTIMPL</c>, as the contract allows.</returns>
    public int EnumSubCommands(out IntPtr enumSubCommands)
    {
        enumSubCommands = IntPtr.Zero;
        EmbeddedLog.Write($"EnumSubCommands -> 0x{HResult.NotImplemented:X8} (no subcommands)");
        return HResult.NotImplemented;
    }

    /// <summary>Reads the file-system path of the first selected item, when it is a directory.</summary>
    /// <param name="itemArray">Selection handed to the command by the shell.</param>
    /// <param name="folder">The selected folder's path.</param>
    /// <returns>Whether a usable folder path was found.</returns>
    private static bool TryGetSelectedFolder(IShellItemArray? itemArray, out string folder)
    {
        folder = string.Empty;

        if (itemArray is null || itemArray.GetItemAt(0, out IShellItem? item) != HResult.Ok || item is null)
        {
            return false;
        }

        string? path = item.GetFileSystemPath();
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        folder = path;
        return true;
    }

    // SID_STopLevelBrowser (shlguid.h) and the interface ids the site route asks for. The service id is
    // what Explorer's site answers on; the interface ids below are the ones the route walks through.
    // IShellView has no field here: the route receives it as an opaque pointer from QueryActiveShellView and
    // only queries IFolderView out of it (OCR round-4 finding 4 — the field that carried its IID was dead).
    private static readonly Guid StopLevelBrowserService = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterface = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid FolderViewInterface = new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");
    private static readonly Guid ShellItemInterface = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>
    /// Candidate interfaces the log reports on the site. The list exists purely as evidence: the shell
    /// documents no contract for what the site is, so recording what it actually supports is what settles
    /// which route can find the viewed folder on the background surface.
    /// </summary>
    private static readonly (string Name, string InterfaceId)[] ProbedSiteInterfaces =
    [
        ("IServiceProvider", "6D5140C1-7436-11CE-8034-00AA006009FA"),
        ("IShellItemArray", "B63EA76D-1F85-456F-A19C-48159EFA858B"),
        ("IShellItem", "43826D1E-E718-42EE-BC55-A1E261C37BFE"),
        ("IShellBrowser", "000214E2-0000-0000-C000-000000000046"),
        ("IShellView", "000214E3-0000-0000-C000-000000000046"),
        ("IFolderView", "CDE725B0-CCC9-4519-917E-325D72FAB4CE"),
        ("IFolderView2", "1AF3A467-214F-4298-908E-06B03E0B39F9"),
        ("IWebBrowser2", "D30C1661-CDAF-11D0-8A3E-00C04FC9E26E"),
    ];

    /// <summary>Reads the folder being viewed from the site the shell handed to <see cref="SetSite"/>.</summary>
    /// <param name="folder">The viewed folder's path, when one was found.</param>
    /// <returns>Whether a usable folder path was found.</returns>
    /// <remarks>
    /// LEARN[34]: the shell does not promise to describe the current folder on the background surface, and
    /// measured behaviour differs between the classic and the Windows 11 context menu. The published route
    /// for that case is the site: <c>IServiceProvider.QueryService(SID_STopLevelBrowser, IShellBrowser)</c>,
    /// then the active shell view, then its <c>IFolderView.GetFolder</c>. Every step is logged with its
    /// HRESULT, because each one can legitimately fail — the site is a different process and only some of
    /// these interfaces are marshalable across it — and the log is what tells the two cases apart.
    /// </remarks>
    private bool TryGetFolderFromSite(out string folder)
    {
        folder = string.Empty;

        object? site = _site;
        if (site is null)
        {
            EmbeddedLog.Write($"site route: SetSite was not called on instance {_instanceId}, so there is no site to ask");
            return false;
        }

        IntPtr viewPointer = IntPtr.Zero;
        IntPtr folderViewPointer = IntPtr.Zero;
        try
        {
            if (site is not IServiceProvider provider)
            {
                EmbeddedLog.Write($"site route: the site is not an IServiceProvider ({DescribeSite(site)})");
                return false;
            }

            Guid service = StopLevelBrowserService;
            Guid browserInterface = ShellBrowserInterface;
            int result = provider.QueryService(ref service, ref browserInterface, out IntPtr browserPointer);
            EmbeddedLog.Write($"site route: QueryService(SID_STopLevelBrowser, IShellBrowser) -> 0x{result:X8}");
            if (result != HResult.Ok || browserPointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPointer);
                result = browser.QueryActiveShellView(out viewPointer);
                EmbeddedLog.Write($"site route: QueryActiveShellView -> 0x{result:X8} (view 0x{viewPointer.ToInt64():X})");
                if (result != HResult.Ok || viewPointer == IntPtr.Zero)
                {
                    return false;
                }
            }
            finally
            {
                Marshal.Release(browserPointer);
            }

            Guid folderViewInterface = FolderViewInterface;
            result = Marshal.QueryInterface(viewPointer, ref folderViewInterface, out folderViewPointer);
            EmbeddedLog.Write($"site route: QueryInterface(IFolderView) -> 0x{result:X8}");
            if (result != HResult.Ok || folderViewPointer == IntPtr.Zero)
            {
                return false;
            }

            var folderView = (IFolderView)Marshal.GetObjectForIUnknown(folderViewPointer);
            Guid itemInterface = ShellItemInterface;
            result = folderView.GetFolder(ref itemInterface, out IntPtr itemPointer);
            EmbeddedLog.Write($"site route: IFolderView.GetFolder -> 0x{result:X8} (item 0x{itemPointer.ToInt64():X})");
            if (result != HResult.Ok || itemPointer == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPointer);
                string? path = item.GetFileSystemPath();
                EmbeddedLog.Write($"site route: the viewed folder is '{path ?? "(no file-system path)"}'");
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    folder = path;
                    return true;
                }
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        catch (Exception exception) when (exception is InvalidCastException or COMException or NotSupportedException or ArgumentException or IOException)
        {
            EmbeddedLog.Write($"site route: failed ({exception.GetType().Name}: {exception.Message})");
        }
        finally
        {
            if (folderViewPointer != IntPtr.Zero)
            {
                Marshal.Release(folderViewPointer);
            }

            if (viewPointer != IntPtr.Zero)
            {
                Marshal.Release(viewPointer);
            }
        }

        return false;
    }

    /// <summary>Names the interfaces a site supports, for the log.</summary>
    /// <param name="site">The site, or <c>null</c>.</param>
    /// <returns>A one-line description; never throws.</returns>
    private static string DescribeSite(object? site)
    {
        if (site is null)
        {
            return "site cleared";
        }

        try
        {
            var supported = new List<string>();
            IntPtr unknown = Marshal.GetIUnknownForObject(site);
            try
            {
                foreach ((string name, string interfaceId) in ProbedSiteInterfaces)
                {
                    Guid requested = new(interfaceId);
                    if (Marshal.QueryInterface(unknown, ref requested, out IntPtr queried) == HResult.Ok)
                    {
                        Marshal.Release(queried);
                        supported.Add(name);
                    }
                }
            }
            finally
            {
                Marshal.Release(unknown);
            }

            return supported.Count == 0
                ? "site supports none of the probed interfaces"
                : $"site supports {string.Join(", ", supported)}";
        }
        catch (Exception exception) when (exception is InvalidCastException or COMException or NotSupportedException or ArgumentException)
        {
            return $"site could not be probed ({exception.GetType().Name}: {exception.Message})";
        }
    }
}

/// <summary>
/// Hosts <see cref="ExplorerCommand"/> as Explorer's out-of-process COM server: it registers a class
/// factory for the command's CLSID and then lets the WPF dispatcher pump the STA messages COM needs.
/// </summary>
/// <remarks>
/// Started by the shell as <c>FolderTreeMD.exe -Embedded</c>, which the package manifest declares in the
/// <c>com:ExeServer</c> element. Registration is the only thing this class does; every method of the
/// command is implemented in <see cref="ExplorerCommand"/>.
/// </remarks>
internal static class ComServer
{
    private const uint ClsCtxLocalServer = 0x4;

    // REGCLS_MULTIPLEUSE keeps one server for every activation the shell makes, and REGCLS_SUSPENDED
    // holds the registration back until the resume call below — the documented way to avoid the shell
    // creating the class object before the server finished registering it.
    private const uint RegClsMultipleUse = 0x1;
    private const uint RegClsSuspended = 0x4;

    private const int CoInitApartmentThreaded = 0x2;
    private const int RpcChangedMode = unchecked((int)0x80010106);

    /// <summary>
    /// Registers the class factory and resumes the class object. Returns <c>false</c> when COM refuses the
    /// registration, so the caller can exit visibly instead of leaving a server that answers nothing.
    /// </summary>
    /// <returns>Whether the class object is registered.</returns>
    internal static bool Register()
    {
        EmbeddedLog.Write($"-Embedded start for CLSID {ExplorerCommandInfo.ClsidString} ({EmbeddedLog.DescribeParentProcess()}; command line: {string.Join(' ', Environment.GetCommandLineArgs())})");

        int initResult = NativeMethods.CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded);
        EmbeddedLog.Write($"CoInitializeEx -> 0x{initResult:X8}");
        if (initResult < 0 && initResult != RpcChangedMode)
        {
            EmbeddedLog.Write("registration refused: COM could not be initialized on this thread");
            return false;
        }

        var factory = new ExplorerCommandFactory();
        int result = NativeMethods.CoRegisterClassObject(
            in ExplorerCommandInfo.Clsid,
            factory,
            ClsCtxLocalServer,
            RegClsMultipleUse | RegClsSuspended,
            out uint cookie);
        EmbeddedLog.Write($"CoRegisterClassObject -> 0x{result:X8} (cookie {cookie})");

        if (result != HResult.Ok)
        {
            EmbeddedLog.Write("registration refused: CoRegisterClassObject failed");
            return false;
        }

        int resumeResult = NativeMethods.CoResumeClassObjects();
        EmbeddedLog.Write($"CoResumeClassObjects -> 0x{resumeResult:X8}");
        if (resumeResult != HResult.Ok)
        {
            NativeMethods.CoRevokeClassObject(cookie);
            EmbeddedLog.Write("registration refused: CoResumeClassObjects failed");
            return false;
        }

        EmbeddedLog.Write("class object registered; server is listening");
        return true;
    }
}

/// <summary>
/// Appends a diagnostic line for every step the shell's COM server takes to
/// <c>%LOCALAPPDATA%\FolderTreeMD\embedded.log</c>.
/// </summary>
/// <remarks>
/// Added at the human's request to settle whether Explorer reaches the class at all when the context-menu
/// entry does not appear: if the log stays empty while the menu has no entry, the shell is not activating
/// the class, which points at the registration mechanism rather than at the command's code. Logging never
/// throws — a diagnostic that breaks the server would be worse than no diagnostic — and it is deliberately
/// append-only, one line per event, so several activations stay distinguishable.
/// </remarks>
internal static class EmbeddedLog
{
    private static readonly object Gate = new();

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FolderTreeMD",
        "embedded.log");

    /// <summary>Appends one timestamped line, tagged with the process id.</summary>
    /// <param name="message">What happened.</param>
    internal static void Write(string message)
    {
        try
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} {message}{Environment.NewLine}";
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, line);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // Deliberately swallowed: see the remarks.
        }
    }

    /// <summary>
    /// Describes the process that started this server, which is the point of the log: an activation from
    /// <c>explorer.exe</c> means the shell reached the class, while an activation from a probe does not.
    /// </summary>
    /// <returns>A short description of the parent process.</returns>
    internal static string DescribeParentProcess()
    {
        try
        {
            var information = default(ProcessBasicInformation);
            int status = NativeMethods.NtQueryInformationProcess(
                Process.GetCurrentProcess().Handle,
                0,
                ref information,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);

            if (status != 0)
            {
                return $"parent unknown (NtQueryInformationProcess 0x{status:X8})";
            }

            int parentId = information.InheritedFromUniqueProcessId.ToInt32();
            string parentName;
            try
            {
                parentName = Process.GetProcessById(parentId).ProcessName;
            }
            catch (ArgumentException)
            {
                parentName = "gone";
            }

            return $"parent pid={parentId} ({parentName})";
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return "parent unknown (ntdll unavailable)";
        }
    }
}

/// <summary>Creates <see cref="ExplorerCommand"/> instances for the shell.</summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ExplorerCommandFactory : IClassFactory
{
    /// <summary>
    /// Creates one command object and hands the shell the interface it asked for, or
    /// <c>E_NOINTERFACE</c> when this class does not implement it. Returning a fixed
    /// <c>IExplorerCommand</c> pointer for every <paramref name="interfaceId"/> would answer, for example, a
    /// marshalling request for <c>IMarshal</c> with a mismatched vtable — undefined behaviour in the shell's
    /// process.
    /// </summary>
    /// <param name="outerUnknown">Aggregating outer unknown; must be null.</param>
    /// <param name="interfaceId">Requested interface.</param>
    /// <param name="instance">The created interface pointer, or zero.</param>
    /// <returns>An HRESULT.</returns>
    public int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance)
    {
        instance = IntPtr.Zero;

        if (outerUnknown != IntPtr.Zero)
        {
            EmbeddedLog.Write($"CreateInstance iid={interfaceId} -> refused (aggregation not supported)");
            return unchecked((int)0x80040110); // CLASS_E_NOAGGREGATION
        }

        IntPtr unknown = Marshal.GetIUnknownForObject(new ExplorerCommand());
        try
        {
            // QueryInterface is answered by the freshly created ExplorerCommand object (through its COM
            // callable wrapper), not by this factory: it returns IExplorerCommand and IUnknown, whatever
            // else the CLR's wrapper adds — IMarshal, measured — and fails with E_NOINTERFACE for the rest.
            // The returned pointer owns one reference, which the caller now owns.
            int result = Marshal.QueryInterface(unknown, ref interfaceId, out instance);
            EmbeddedLog.Write($"CreateInstance iid={interfaceId} -> 0x{result:X8} (pointer zero: {instance == IntPtr.Zero})");
            return result;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Keeps the server alive for the shell. Nothing is tracked, so this is a no-op.</summary>
    /// <param name="keepAlive">Whether the shell wants the server kept alive.</param>
    /// <returns>An HRESULT.</returns>
    public int LockServer(bool keepAlive)
    {
        EmbeddedLog.Write($"LockServer({keepAlive}) -> 0x00000000");
        return HResult.Ok;
    }
}

/// <summary>The structure <c>NtQueryInformationProcess</c> fills for <c>ProcessBasicInformation</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessBasicInformation
{
    /// <summary>Reserved.</summary>
    internal IntPtr Reserved1;

    /// <summary>Address of the process environment block.</summary>
    internal IntPtr PebBaseAddress;

    /// <summary>Reserved.</summary>
    internal IntPtr Reserved2_0;

    /// <summary>Reserved.</summary>
    internal IntPtr Reserved2_1;

    /// <summary>This process's id.</summary>
    internal IntPtr UniqueProcessId;

    /// <summary>The id of the process that created this one — the field the log needs.</summary>
    internal IntPtr InheritedFromUniqueProcessId;
}

/// <summary>The HRESULTs this feature needs to name.</summary>
public static class HResult
{
    /// <summary>Success.</summary>
    internal const int Ok = 0;

    /// <summary><c>E_FAIL</c>: the command could not be carried out.</summary>
    internal const int Fail = unchecked((int)0x80004005);

    /// <summary><c>E_NOTIMPL</c>: the method is not implemented, which the contract permits for subcommands.</summary>
    internal const int NotImplemented = unchecked((int)0x80004001);
}

/// <summary>The command's menu state (<c>EXPCMDSTATE</c>).</summary>
public enum EXPCMDSTATE
{
    /// <summary>The entry is shown and can be clicked.</summary>
    Enabled = 0,

    /// <summary>The entry is shown greyed out.</summary>
    Disabled = 1,

    /// <summary>The entry is not shown.</summary>
    Hidden = 2,

    /// <summary>The entry is a checkbox.</summary>
    Checkbox = 4,

    /// <summary>The checkbox is checked.</summary>
    Checked = 8,

    /// <summary>The entry is a radio button.</summary>
    RadioCheck = 16,
}

/// <summary>
/// The command's flags (<c>EXPCMDFLAGS</c>). Names and values are the SDK's, from
/// <c>ShObjIdl_core.h</c> (<c>enum _EXPCMDFLAGS</c>), so a later reader can look them up instead of
/// trusting this file.
/// </summary>
[Flags]
public enum EXPCMDFLAGS
{
    /// <summary>Nothing special: one command, no subcommands.</summary>
    Default = 0,

    /// <summary>The command opens a submenu.</summary>
    HasSubCommands = 0x1,

    /// <summary>The command has a split button.</summary>
    HasSplitButton = 0x2,

    /// <summary>The command's label is hidden.</summary>
    HideLabel = 0x4,

    /// <summary>The command is a separator.</summary>
    IsSeparator = 0x8,

    /// <summary>The command shows the UAC shield.</summary>
    HasLuaShield = 0x10,

    /// <summary>A separator is drawn before the command.</summary>
    SeparatorBefore = 0x20,

    /// <summary>A separator is drawn after the command.</summary>
    SeparatorAfter = 0x40,

    /// <summary>The command renders as a drop-down.</summary>
    IsDropDown = 0x80,

    /// <summary>The command can be toggled on and off.</summary>
    Toggleable = 0x100,

    /// <summary>The command uses the automatic menu icons.</summary>
    AutoMenuIcons = 0x200,
}

/// <summary>How a shell item should report its name (<c>SIGDN</c>).</summary>
public enum SIGDN
{
    /// <summary>The item's file-system path.</summary>
    FileSystemPath = unchecked((int)0x80058000),
}

/// <summary>
/// <c>IExplorerCommand</c> (<c>shobjidl_core.h</c>). Every method uses <see cref="PreserveSigAttribute"/>
/// because the shell reads the HRESULT and, for some of them, an empty result as "no value".
/// </summary>
[ComImport]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IExplorerCommand
{
    /// <summary>Supplies the label.</summary>
    /// <param name="itemArray">Selection.</param>
    /// <param name="title">Label.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetTitle(IShellItemArray? itemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? title);

    /// <summary>Supplies the icon reference.</summary>
    /// <param name="itemArray">Selection.</param>
    /// <param name="icon">Icon reference.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetIcon(IShellItemArray? itemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? icon);

    /// <summary>Supplies the tooltip.</summary>
    /// <param name="itemArray">Selection.</param>
    /// <param name="toolTip">Tooltip.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetToolTip(IShellItemArray? itemArray, [MarshalAs(UnmanagedType.LPWStr)] out string? toolTip);

    /// <summary>Supplies the canonical name.</summary>
    /// <param name="canonicalName">Canonical name.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetCanonicalName(out Guid canonicalName);

    /// <summary>Supplies the menu state.</summary>
    /// <param name="itemArray">Selection.</param>
    /// <param name="okToBeSlow">Whether a slow answer is acceptable.</param>
    /// <param name="state">State.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetState(IShellItemArray? itemArray, [MarshalAs(UnmanagedType.Bool)] bool okToBeSlow, out EXPCMDSTATE state);

    /// <summary>Runs the command.</summary>
    /// <param name="itemArray">Selection.</param>
    /// <param name="bindContext">Bind context.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int Invoke(IShellItemArray? itemArray, IntPtr bindContext);

    /// <summary>Supplies the command flags.</summary>
    /// <param name="flags">Flags.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetFlags(out EXPCMDFLAGS flags);

    /// <summary>Supplies the subcommands.</summary>
    /// <param name="enumSubCommands">Subcommand enumerator.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int EnumSubCommands(out IntPtr enumSubCommands);
}

/// <summary>
/// <c>IShellItemArray</c> (<c>shobjidl_core.h</c>), declaring only the slots this feature calls: the
/// earlier methods are present but unused, because the vtable positions of <c>GetCount</c> and
/// <c>GetItemAt</c> are what matter.
/// </summary>
[ComImport]
[Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItemArray
{
    /// <summary>Unused.</summary>
    /// <param name="bindContext">Bind context.</param>
    /// <param name="handler">Handler id.</param>
    /// <param name="interfaceId">Interface id.</param>
    /// <param name="result">Result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);

    /// <summary>Unused.</summary>
    /// <param name="flags">Flags.</param>
    /// <param name="interfaceId">Interface id.</param>
    /// <param name="result">Result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetPropertyStore(int flags, ref Guid interfaceId, out IntPtr result);

    /// <summary>Unused.</summary>
    /// <param name="propertyKey">Property key.</param>
    /// <param name="interfaceId">Interface id.</param>
    /// <param name="result">Result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetPropertyDescriptionList(IntPtr propertyKey, ref Guid interfaceId, out IntPtr result);

    /// <summary>Unused.</summary>
    /// <param name="flags">Flags.</param>
    /// <param name="mask">Attribute mask.</param>
    /// <param name="attributes">Attributes.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetAttributes(int flags, uint mask, out uint attributes);

    /// <summary>Counts the selected items.</summary>
    /// <param name="count">Item count.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetCount(out int count);

    /// <summary>Gets one selected item.</summary>
    /// <param name="index">Item index.</param>
    /// <param name="item">The item.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetItemAt(int index, out IShellItem? item);
}

/// <summary>
/// <c>IShellItem</c> (<c>shobjidl_core.h</c>), declaring only the slots this feature calls.
/// </summary>
[ComImport]
[Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellItem
{
    /// <summary>Unused.</summary>
    /// <param name="bindContext">Bind context.</param>
    /// <param name="handler">Handler id.</param>
    /// <param name="interfaceId">Interface id.</param>
    /// <param name="result">Result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid interfaceId, out IntPtr result);

    /// <summary>Unused.</summary>
    /// <param name="parent">Parent item.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetParent(out IShellItem? parent);

    /// <summary>Gets the item's display name in the requested form.</summary>
    /// <param name="displayName">Which form of name to return.</param>
    /// <param name="name">The name.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetDisplayName(SIGDN displayName, [MarshalAs(UnmanagedType.LPWStr)] out string? name);

    /// <summary>Unused.</summary>
    /// <param name="mask">Attribute mask.</param>
    /// <param name="attributes">Attributes.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetAttributes(uint mask, out uint attributes);

    /// <summary>Unused.</summary>
    /// <param name="other">Other item.</param>
    /// <param name="hint">Comparison hint.</param>
    /// <param name="order">Comparison result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int Compare(IShellItem? other, uint hint, out int order);
}

/// <summary>
/// <c>IObjectWithSite</c> (<c>ocidl.h</c>), implemented by <see cref="ExplorerCommand"/> because the shell
/// offers the site through it — the route by which the background surface's folder is reachable.
/// </summary>
[ComImport]
[Guid("FC4801A3-2BA9-11CF-A229-00AA003D7352")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IObjectWithSite
{
    /// <summary>Accepts the site.</summary>
    /// <param name="site">The site, or <c>null</c> to clear it.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SetSite([MarshalAs(UnmanagedType.Interface)] object? site);

    /// <summary>Returns the site, for the requested interface.</summary>
    /// <param name="interfaceId">Interface the caller asks for.</param>
    /// <param name="site">The interface pointer, or zero.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetSite(ref Guid interfaceId, out IntPtr site);
}

/// <summary><c>IServiceProvider</c> (<c>servprov.h</c>), the first step of the site route.</summary>
[ComImport]
[Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IServiceProvider
{
    /// <summary>Asks the site for one of its services.</summary>
    /// <param name="service">Service id.</param>
    /// <param name="interfaceId">Interface to return the service as.</param>
    /// <param name="serviceInstance">The service, or zero.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int QueryService(ref Guid service, ref Guid interfaceId, out IntPtr serviceInstance);
}

/// <summary>
/// <c>IShellBrowser</c> (<c>shobjidl_core.h</c>), declaring the earlier slots only to reach
/// <c>QueryActiveShellView</c>: the vtable position is what matters, and the unused methods are never
/// called.
/// </summary>
[ComImport]
[Guid("000214E2-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellBrowser
{
    /// <summary>Unused.</summary>
    /// <param name="window">Window handle.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetWindow(out IntPtr window);

    /// <summary>Unused.</summary>
    /// <param name="help">Whether help mode is entering.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool help);

    /// <summary>Unused.</summary>
    /// <param name="menu">Menu handle.</param>
    /// <param name="owner">Owner window handle.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int InsertMenusSB(IntPtr menu, IntPtr owner);

    /// <summary>Unused.</summary>
    /// <param name="sharedMenu">Shared menu handle.</param>
    /// <param name="menu">Menu handle.</param>
    /// <param name="owner">Owner window handle.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SetMenuSB(IntPtr sharedMenu, IntPtr menu, IntPtr owner);

    /// <summary>Unused.</summary>
    /// <param name="owner">Owner window handle.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int RemoveMenusSB(IntPtr owner);

    /// <summary>Unused.</summary>
    /// <param name="text">Status text.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SetStatusTextSB(IntPtr text);

    /// <summary>Unused.</summary>
    /// <param name="enable">Whether to enable the modeless state.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);

    /// <summary>Unused.</summary>
    /// <param name="message">Message.</param>
    /// <param name="identifier">Command id.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int TranslateAcceleratorSB(IntPtr message, ushort identifier);

    /// <summary>Unused.</summary>
    /// <param name="pidl">Item id list.</param>
    /// <param name="flags">Browse flags.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int BrowseObject(IntPtr pidl, uint flags);

    /// <summary>Unused.</summary>
    /// <param name="aspect">Stream aspect.</param>
    /// <param name="stream">The stream.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetViewStateStream(uint aspect, out IntPtr stream);

    /// <summary>Unused.</summary>
    /// <param name="message">Control message.</param>
    /// <param name="window">Control window handle.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetControlWindow(uint message, out IntPtr window);

    /// <summary>Unused. Native shape: <c>(UINT id, UINT uMsg, WPARAM wParam, LPARAM lParam, LRESULT *pret)</c>.</summary>
    /// <param name="identifier">Control id the browser answers for.</param>
    /// <param name="message">Control message.</param>
    /// <param name="wParam">First message argument (pointer-sized, as <c>WPARAM</c> is).</param>
    /// <param name="lParam">Second message argument.</param>
    /// <param name="result">Message result.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SendControlMsg(uint identifier, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);

    /// <summary>Returns the view the browser is showing — the step that leads to the viewed folder.</summary>
    /// <param name="view">The active view.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int QueryActiveShellView(out IntPtr view);
}

/// <summary>
/// <c>IFolderView</c> (<c>shobjidl_core.h</c>), declaring the earlier slots only to reach
/// <c>GetFolder</c>.
/// </summary>
[ComImport]
[Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IFolderView
{
    /// <summary>Unused.</summary>
    /// <param name="mode">View mode.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetCurrentViewMode(out uint mode);

    /// <summary>Unused.</summary>
    /// <param name="mode">View mode.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int SetCurrentViewMode(uint mode);

    /// <summary>Returns the folder the view is showing.</summary>
    /// <param name="interfaceId">Interface to return it as.</param>
    /// <param name="folder">The folder.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int GetFolder(ref Guid interfaceId, out IntPtr folder);
}

/// <summary><c>IClassFactory</c> (<c>unknwn.h</c>), implemented by <see cref="ExplorerCommandFactory"/>.</summary>
[ComImport]
[Guid("00000001-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IClassFactory
{
    /// <summary>Creates one instance of the class.</summary>
    /// <param name="outerUnknown">Aggregating outer unknown.</param>
    /// <param name="interfaceId">Requested interface.</param>
    /// <param name="instance">The created interface pointer.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int CreateInstance(IntPtr outerUnknown, ref Guid interfaceId, out IntPtr instance);

    /// <summary>Locks the server in memory.</summary>
    /// <param name="keepAlive">Whether to keep the server alive.</param>
    /// <returns>An HRESULT.</returns>
    [PreserveSig]
    int LockServer([MarshalAs(UnmanagedType.Bool)] bool keepAlive);
}

/// <summary>Convenience accessors over the shell interfaces.</summary>
internal static class ShellItemExtensions
{
    /// <summary>Returns the item's file-system path, or <c>null</c> when it has none.</summary>
    /// <param name="item">The shell item.</param>
    /// <returns>The file-system path, or <c>null</c>.</returns>
    internal static string? GetFileSystemPath(this IShellItem item) =>
        item.GetDisplayName(SIGDN.FileSystemPath, out string? path) == HResult.Ok ? path : null;
}

/// <summary>The COM entry points this feature calls.</summary>
/// <remarks>
/// <see cref="DllImportAttribute"/> rather than the source-generated <c>LibraryImport</c>: the generated
/// marshaller does not support the COM interface parameter <c>CoRegisterClassObject</c> needs.
/// </remarks>
internal static class NativeMethods
{
    /// <summary>Reads basic process information, used only to name the process that started the server.</summary>
    /// <param name="processHandle">Handle of this process.</param>
    /// <param name="processInformationClass">Zero selects <c>ProcessBasicInformation</c>.</param>
    /// <param name="processInformation">Receives the information.</param>
    /// <param name="processInformationLength">Size of the buffer.</param>
    /// <param name="returnLength">Bytes written.</param>
    /// <returns>An NTSTATUS.</returns>
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    /// <summary>Initializes COM on this thread.</summary>
    /// <param name="reserved">Reserved; must be zero.</param>
    /// <param name="coInit">Apartment model.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    internal static extern int CoInitializeEx(IntPtr reserved, int coInit);

    /// <summary>Publishes a class factory for a CLSID.</summary>
    /// <param name="clsid">The class id.</param>
    /// <param name="classFactory">The factory, marshalled as its <c>IClassFactory</c>.</param>
    /// <param name="classContext">Where the class runs.</param>
    /// <param name="flags">Registration behaviour.</param>
    /// <param name="cookie">Registration cookie, for revocation.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    internal static extern int CoRegisterClassObject(
        in Guid clsid,
        [MarshalAs(UnmanagedType.Interface)] object classFactory,
        uint classContext,
        uint flags,
        out uint cookie);

    /// <summary>Lets the shell see the class objects registered while suspended.</summary>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    internal static extern int CoResumeClassObjects();

    /// <summary>Withdraws a class object registration.</summary>
    /// <param name="cookie">Registration cookie.</param>
    /// <returns>An HRESULT.</returns>
    [DllImport("ole32.dll")]
    internal static extern int CoRevokeClassObject(uint cookie);
}
