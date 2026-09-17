namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Creates a throwaway fixture tree under the system temp directory and cleans it up again, so
/// every test runs against a real filesystem instead of a mock.
/// </summary>
/// <remarks>
/// The root directory name is a GUID, which gives each test an isolated tree and keeps assertions
/// independent of the machine's temp path. <see cref="Dispose"/> is defensive: a test that
/// deliberately makes part of the tree unreadable or replaces it with a reparse point must not turn
/// a passing assertion into a teardown crash.
/// </remarks>
public sealed class TestTreeBuilder : IDisposable
{
    /// <summary>Initializes a builder and creates its empty root directory.</summary>
    public TestTreeBuilder()
    {
        Root = Path.Combine(Path.GetTempPath(), "FolderTreeMD.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Absolute path of the fixture root; also the root of every generated listing.</summary>
    public string Root { get; }

    /// <summary>Name of the fixture root, i.e. the text of the generated root line.</summary>
    public string RootName => Path.GetFileName(Root);

    /// <summary>Creates a directory relative to the fixture root.</summary>
    /// <param name="relativePath">Path relative to the root, using <c>/</c>; parents are created.</param>
    /// <returns>The absolute path of the created directory.</returns>
    public string AddDirectory(string relativePath)
    {
        string path = FullPath(relativePath);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Creates a file with the given content, creating its parent directory if needed.</summary>
    /// <param name="relativePath">Path relative to the root, using <c>/</c>.</param>
    /// <param name="content">File content; defaults to a single character.</param>
    /// <returns>The absolute path of the created file.</returns>
    public string AddFile(string relativePath, string content = "x")
    {
        string path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Creates a file of an exact byte length, for the size-format and folder-sum tests.
    /// </summary>
    /// <param name="relativePath">Path relative to the root, using <c>/</c>.</param>
    /// <param name="byteCount">Exact size in bytes.</param>
    /// <returns>The absolute path of the created file.</returns>
    public string AddFileOfSize(string relativePath, int byteCount)
    {
        string path = FullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[byteCount]);
        return path;
    }

    /// <summary>Marks an existing entry as hidden (the attribute UI_SPEC §5 rule 5 filters on).</summary>
    /// <param name="relativePath">Path relative to the root, using <c>/</c>.</param>
    public void MarkHidden(string relativePath)
    {
        string path = FullPath(relativePath);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
    }

    /// <summary>Maps a root-relative path to an absolute one.</summary>
    /// <param name="relativePath">Path relative to the root, using <c>/</c>.</param>
    /// <returns>The absolute path.</returns>
    public string FullPath(string relativePath) =>
        Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Deletes the fixture tree after making it deletable: every directory link is unlinked and every
    /// read-only flag is cleared, because <see cref="Directory.Delete(string, bool)"/> neither unlinks a
    /// junction (it walks through it — a junction back at an ancestor loops forever) nor deletes a
    /// read-only file.
    /// </summary>
    /// <remarks>
    /// Two pre-existing leaks are closed here (M6 fix round 3, item 4; measured: 78 orphaned trees had
    /// accumulated on this machine). First, only the root's immediate links used to be unlinked, so the
    /// fixtures that nest a junction (<c>mid/deep/loop</c>, <c>App/shared</c>) survived every run.
    /// Second, the attribute tests set <c>ReadOnly</c> on the files they assert about (<c>[HRSA]</c>),
    /// which made the recursive delete fail. Both failures were swallowed by the catch below, so nothing
    /// ever reported them.
    ///
    /// The pre-pass runs **inside** the guarded block (M6 fix round 4, item 1) because it walks and
    /// rewrites attributes: it can throw on an entry that vanished or that this process may not touch, and
    /// a defensive teardown must not turn a passing assertion into a crash.
    ///
    /// **Skipping the recursive delete when the pre-pass fails is deliberate** (M7 item 1, from OCR run-5
    /// finding 1): the only reason to run the delete is that every link has been unlinked, and
    /// <see cref="Directory.Delete(string, bool)"/> walks *through* a junction rather than unlinking it —
    /// so deleting a tree whose links are still in place is exactly the operation that could reach into
    /// another tree. A failed pre-pass therefore leaves the fixture in place instead: an orphaned temp
    /// directory is a lesser fault than deleting someone else's data. Do not "fix" this by attempting the
    /// delete anyway.
    /// </remarks>
    public void Dispose()
    {
        if (!Directory.Exists(Root))
        {
            return;
        }

        try
        {
            PrepareForDeletion(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort by design: see the remark above — the tree stays rather than the delete running
            // with un-unlinked links in it.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Unlinks the directory links and clears the read-only flags in one subtree, without ever descending
    /// through a link: a link is removed, not followed, so no walk can leave this fixture's own tree.
    /// </summary>
    /// <param name="path">Directory to prepare.</param>
    /// <remarks>
    /// The entries are materialised before any of them is deleted (M6 fix round 4, item 2): mutating a
    /// directory while its enumeration is still open can skip entries, and only the deletions need the
    /// list to be stable. The read-only flag is cleared on each entry exactly once and only where its own
    /// deletion needs it (M7 item 4, from OCR run-5 finding 2): on a link before it is unlinked — a
    /// read-only link cannot be deleted, measured — on a file in this loop, and on a directory inside its
    /// own recursive call. A note on the enumeration itself, because OCR run-4 finding 1 claimed
    /// otherwise: the parameterless overload does not skip hidden or system entries — measured, and
    /// <c>EnumerationOptions.Compatible.AttributesToSkip</c> is <c>None</c> on this runtime; the finding
    /// confused it with the <c>Hidden | System</c> default of <c>new EnumerationOptions()</c>.
    /// </remarks>
    private static void PrepareForDeletion(string path)
    {
        ClearReadOnlyAttribute(path);

        var entries = new List<string>(Directory.EnumerateFileSystemEntries(path));

        foreach (string entry in entries)
        {
            FileAttributes attributes = File.GetAttributes(entry);

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                // A link carries attributes of its own (measured: clearing through a junction leaves the
                // target alone), and a read-only link fails its own delete — so clear before unlinking.
                ClearReadOnlyAttribute(entry);

                // Deleting the link removes the link itself, never its target.
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry);
                }
                else
                {
                    File.Delete(entry);
                }

                continue;
            }

            if ((attributes & FileAttributes.Directory) != 0)
            {
                PrepareForDeletion(entry); // clears this directory's own flag at its top
            }
            else
            {
                ClearReadOnlyAttribute(entry);
            }
        }
    }

    /// <summary>Removes the read-only flag from one entry, if it is set.</summary>
    /// <param name="path">File or directory to adjust.</param>
    private static void ClearReadOnlyAttribute(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }
    }
}
