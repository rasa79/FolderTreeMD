using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderTreeMD.Core;

/// <summary>
/// The identity of a file-system object as the OS reports it: volume serial number plus file index,
/// which together are unique for the lifetime of the object on that volume.
/// </summary>
/// <param name="VolumeSerialNumber">Serial number of the volume holding the object.</param>
/// <param name="FileIndex">Index of the object within that volume.</param>
// LEARN[6]: cycle detection uses (VolumeSerialNumber, FileIndex) read through a manual
// GetFileInformationByHandle P/Invoke rather than a path comparison or a package.
// Alternatives considered: (a) compare resolved target paths — rejected: a cycle can be reached
//   through different path spellings (short 8.3 names, `\\?\` prefixes, different case, mapped
//   drives), so string comparison reports "different" for the same directory and the loop survives;
//   (b) use the `CsWin32` source generator or `System.IO.FileSystem.AccessControl` — rejected:
//   UI_SPEC §6 specifies GetFileInformationByHandle and PROJECT RULES forbid adding dependencies
//   without per-milestone approval; (c) `FileSystemInfo.LinkTarget` chains — rejected: it only
//   describes one level and says nothing about hard-linked or re-entered directories.
// Pros of chosen approach: the identity comes from the OS handle itself, so it is stable regardless
//   of how the directory was reached; one dependency-free P/Invoke; the traversal records identities
//   in a set and can answer "have I already been here" in O(1).
// Cons of chosen approach: the identity only exists while the handle is open, so it must be read
//   per directory (one extra CreateFile call each); the struct layout is Windows-specific, so this
//   file is not portable — acceptable, the project is Windows-only (PLAN.md out-of-scope list).
// See also: LEARN[2]
internal readonly record struct FileIdentity(ulong VolumeSerialNumber, ulong FileIndex);

/// <summary>
/// Reads <see cref="FileIdentity"/> values for directories so the engine can detect cycles when
/// following symlinks/junctions. Manual P/Invoke, no external package (UI_SPEC §6).
/// </summary>
internal static class FileIdentityReader
{
    private const uint FileFlagBackupSemantics = 0x02000000;

    /// <summary>
    /// Reads the identity of a directory, resolving any reparse point to its target.
    /// </summary>
    /// <param name="path">Absolute path of the directory.</param>
    /// <returns>
    /// The directory's identity, or <c>null</c> when the OS would not open it — the caller then
    /// treats the directory as "not yet visited", which can only cause a redundant visit, never a
    /// missed one.
    /// </returns>
    /// <remarks>
    /// The handle deliberately does <b>not</b> pass FILE_FLAG_OPEN_REPARSE_POINT: for a junction or
    /// symlink the identity of the link itself is useless for cycle detection — what must be compared
    /// is the directory the link resolves to, which is the identity the traversal records when it
    /// enters that directory. Resolution happens only when a link is actually followed, so the
    /// not-followed path never touches the target.
    /// </remarks>
    public static FileIdentity? TryRead(string path)
    {
        // FILE_FLAG_BACKUP_SEMANTICS is what lets CreateFile open a *directory* for metadata.
        using SafeFileHandle handle = CreateFileW(
            path,
            dwDesiredAccess: 0,
            dwShareMode: FileShare.ReadWrite | FileShare.Delete,
            lpSecurityAttributes: IntPtr.Zero,
            dwCreationDisposition: FileMode.Open,
            dwFlagsAndAttributes: FileFlagBackupSemantics,
            hTemplateFile: IntPtr.Zero);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out ByHandleFileInformation information))
        {
            return null;
        }

        ulong fileIndex = ((ulong)information.FileIndexHigh << 32) | information.FileIndexLow;
        return new FileIdentity(information.VolumeSerialNumber, fileIndex);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        FileShare dwShareMode,
        IntPtr lpSecurityAttributes,
        FileMode dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
