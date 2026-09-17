using System.Diagnostics;
using System.Text;
using FolderTreeMD.Core;

// LEARN[5]: the access-denied and reparse-classification tests swap process-wide seams on the type
// under test, so xUnit's default parallel test collections must be off for this assembly.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace FolderTreeMD.Core.Tests;

/// <summary>
/// Tests for <see cref="MarkdownListing"/> against real fixture trees. Every test name carries the
/// <c>UI_SPEC.md</c> §5 rule or §6 contract it covers, so a failing test points at the spec line.
/// </summary>
public class MarkdownListingTests
{
    /// <summary>
    /// Options for the traversal/grammar tests: annotations off, so a failure points at the traversal
    /// rule under test rather than at a size or attribute suffix. M3's own tests enable them
    /// explicitly.
    /// </summary>
    /// <returns>Listing options with both annotation settings off.</returns>
    private static ListingOptions BaseOptions() => new() { ShowFileSizes = false, ShowAttributes = false };

    /// <summary>
    /// Walking-skeleton smoke test, kept from M1: two folders and two files, no annotations, exact
    /// expected markdown.
    /// </summary>
    [Fact]
    public void SmokeTest_TwoFoldersTwoFiles_ExactMarkdown()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");
        tree.AddDirectory("beta");
        tree.AddFile("root.txt");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [alpha]\n" +
            "        - inner.txt\n" +
            "    - [beta]\n" +
            "    - root.txt\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>UI_SPEC §5 rule 1: relative paths use <c>/</c>, never the Windows <c>\</c>.</summary>
    [Fact]
    public void Rule1_ForwardSlashSeparators()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("a/b/c/deep.txt");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        Assert.Contains("- [a/b/c]", markdown);
        Assert.DoesNotContain('\\', markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 4: folders first, then files, each group sorted alphabetically
    /// case-insensitively (<see cref="StringComparer.OrdinalIgnoreCase"/>).
    /// </summary>
    [Fact]
    public void Rule4_FoldersFirstThenFilesOrdinalIgnoreCase()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("Banana.txt");
        tree.AddFile("apple.txt");
        tree.AddFile("cherry.txt");
        tree.AddDirectory("Delta");
        tree.AddDirectory("echo");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [Delta]\n" +
            "    - [echo]\n" +
            "    - apple.txt\n" +
            "    - Banana.txt\n" +
            "    - cherry.txt\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>UI_SPEC §5 rule 5: hidden entries are excluded entirely — directories and files alike.</summary>
    [Fact]
    public void Rule5_HiddenExcludedUnlessEnabled()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("visible.txt");
        tree.AddFile("secret.txt");
        tree.MarkHidden("secret.txt");
        tree.AddDirectory("hiddenFolder");
        tree.AddFile("hiddenFolder/inside.txt");
        tree.MarkHidden("hiddenFolder");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - visible.txt\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>UI_SPEC §5 rule 5: with <c>includeHidden</c> on, hidden directories and files appear.</summary>
    [Fact]
    public void Rule5_HiddenIncludedWhenEnabled()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("visible.txt");
        tree.AddFile("secret.txt");
        tree.MarkHidden("secret.txt");
        tree.AddDirectory("hiddenFolder");
        tree.AddFile("hiddenFolder/inside.txt");
        tree.MarkHidden("hiddenFolder");

        ListingOptions options = BaseOptions();
        options.IncludeHidden = true;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [hiddenFolder]\n" +
            "        - inside.txt\n" +
            "    - secret.txt\n" +
            "    - visible.txt\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 6: a folder at <c>level == depth</c> keeps its own line and its children are
    /// omitted; a folder above the limit keeps its files.
    /// </summary>
    [Fact]
    public void Rule6_DepthCutsChildrenButKeepsFolderLine()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("top/mid/deep/leaf.txt");
        tree.AddFile("top/direct.txt");

        ListingOptions options = BaseOptions();
        options.Depth = 1;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        // The root is above the limit, so its children are read; [top] is at the limit, so neither
        // its files nor its subfolders are listed (UI_SPEC §5 rule 6: "a folder's children").
        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [top]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>UI_SPEC §5 rule 6: <c>depth 0</c> means the root line only.</summary>
    [Fact]
    public void Rule6_DepthZeroIsRootOnly()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");
        tree.AddFile("root.txt");

        ListingOptions options = BaseOptions();
        options.Depth = 0;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        Assert.Equal($"- [{tree.RootName}]\n", markdown);
    }

    /// <summary>UI_SPEC §5 rule 8: the output ends with exactly one trailing newline.</summary>
    [Fact]
    public void Rule8_SingleTrailingNewline()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        Assert.EndsWith("\n", markdown, StringComparison.Ordinal);
        Assert.False(markdown.EndsWith("\n\n", StringComparison.Ordinal), "output must not end with a blank line");
        Assert.Equal(3, markdown.Split('\n').Length - 1);
    }

    /// <summary>
    /// UI_SPEC §5 table / §6: an unreadable nested directory is emitted as
    /// <c>- [{relative/path}] (access denied)</c> and the traversal continues with its siblings.
    /// </summary>
    [Fact]
    public void AccessDeniedFolderMarkedAndSiblingsContinue()
    {
        using var tree = new TestTreeBuilder();
        string deniedPath = tree.AddDirectory("denied");
        tree.AddFile("denied/never-listed.txt");
        tree.AddFile("sibling/kept.txt");

        Func<string, string[]> original = MarkdownListing.EnumerateDirectories;
        MarkdownListing.EnumerateDirectories = path =>
            path.Equals(deniedPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"simulated denial for {path}")
                : original(path);

        try
        {
            string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [denied] (access denied)\n" +
                "    - [sibling]\n" +
                "        - kept.txt\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateDirectories = original;
        }
    }

    /// <summary>
    /// UI_SPEC §5 table: a directory junction is emitted as
    /// <c>- [{relative/path}] (link → {target})</c> and its target is not enumerated.
    /// </summary>
    [Fact]
    public void LinkNotFollowed_EmitsLinkLine()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("target");
        tree.AddFile("target/inside.txt");
        string link = CreateDirectoryLink(tree, "link", target);

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [link] (link → " + linkTarget + ")\n" +
            "    - [target]\n" +
            "        - inside.txt\n";
        Assert.Equal(expected, markdown);
        Assert.DoesNotContain("link/inside.txt", markdown, StringComparison.Ordinal);
    }

    /// <summary>
    /// M2-review carry-over 1a: a not-followed link line carries <b>only</b> the link annotation —
    /// no size and no attribute suffixes, even with every annotation setting on.
    /// </summary>
    [Fact]
    public void LinkNotFollowed_HasNoSizeOrAttributeSuffix()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("heavy/inside.bin", 2048); // would be "(2 KB)" if the link were annotated
        string link = CreateDirectoryLink(tree, "link", tree.FullPath("heavy"));

        var options = new ListingOptions { ShowFileSizes = true, ShowFolderSizes = true, ShowAttributes = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}] (2 KB)\n" +
            "    - [heavy] (2 KB)\n" +
            "        - inside.bin (2 KB) [A]\n" +
            "    - [link] (link → " + linkTarget + ")\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// UI_SPEC §5 table: a file that is a reparse point gets the same link line as a directory link.
    /// </summary>
    /// <remarks>
    /// This machine cannot create file symlinks (Windows requires Developer Mode or elevation, and
    /// <c>mklink</c> without <c>/J</c> needs the same privilege), so the entry is presented through
    /// the internal inspection seam with a real file as its stored target. The gap is recorded as
    /// KNOWN_LIMITATIONS L4.
    /// </remarks>
    [Fact]
    public void FileLink_EmitsLinkLine()
    {
        using var tree = new TestTreeBuilder();
        string realFile = tree.AddFile("real.txt");
        string ghostPath = Path.Combine(tree.Root, "ghost-link.txt");

        bool createdRealFileLink = TryCreateFileSymlink(ghostPath, realFile);
        Func<string, string[]> originalEnumerate = MarkdownListing.EnumerateFiles;
        Func<string, MarkdownListing.EntryInfo?> originalInspect = MarkdownListing.InspectEntry;

        if (!createdRealFileLink)
        {
            // No real symlink available: present the entry through the seams. The path is injected
            // only on this route — a real symlink inside the root is already returned by enumeration,
            // and injecting it again would list it twice.
            MarkdownListing.EnumerateFiles = path =>
                path.Equals(tree.Root, StringComparison.OrdinalIgnoreCase)
                    ? [.. originalEnumerate(path), ghostPath]
                    : originalEnumerate(path);
            MarkdownListing.InspectEntry = path => path.Equals(ghostPath, StringComparison.OrdinalIgnoreCase)
                ? new MarkdownListing.EntryInfo(FileAttributes.ReparsePoint | FileAttributes.Normal, realFile)
                : originalInspect(path);
        }

        try
        {
            // Sizes and attributes stay off for this test: a file link is not a normal file entry, so
            // the §5 table gives it only the link annotation (see LinkNotFollowed_HasNoSizeOrAttributeSuffix).
            string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

            string expectedTarget = createdRealFileLink ? new FileInfo(ghostPath).LinkTarget! : realFile;
            string expected =
                $"- [{tree.RootName}]\n" +
                "    - ghost-link.txt (link → " + expectedTarget + ")\n" +
                "    - real.txt\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateFiles = originalEnumerate;
            MarkdownListing.InspectEntry = originalInspect;
        }
    }

    /// <summary>
    /// A reparse point whose tag carries no target (a volume mount point or cloud placeholder) has
    /// no line in the UI_SPEC §5 grammar, so it is omitted rather than invented — KNOWN_LIMITATIONS
    /// L4. The entry must also never be expanded.
    /// </summary>
    /// <remarks>
    /// A real mount point cannot be created without elevation, and the reparse attribute cannot be
    /// forced onto an ordinary file (the OS ignores it), so the entry is presented through the
    /// internal inspection seam.
    /// </remarks>
    [Fact]
    public void ReparsePointWithoutTarget_IsOmitted()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("plain.txt");
        string mountLike = Path.Combine(tree.Root, "mount-like.txt");

        Func<string, string[]> originalEnumerate = MarkdownListing.EnumerateFiles;
        Func<string, MarkdownListing.EntryInfo?> originalInspect = MarkdownListing.InspectEntry;

        MarkdownListing.EnumerateFiles = path =>
            path.Equals(tree.Root, StringComparison.OrdinalIgnoreCase)
                ? [.. originalEnumerate(path), mountLike]
                : originalEnumerate(path);
        MarkdownListing.InspectEntry = path => path.Equals(mountLike, StringComparison.OrdinalIgnoreCase)
            ? new MarkdownListing.EntryInfo(FileAttributes.ReparsePoint | FileAttributes.Normal, LinkTarget: null)
            : originalInspect(path);

        try
        {
            string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

            string expected =
                $"- [{tree.RootName}]\n" +
                "    - plain.txt\n";
            Assert.Equal(expected, markdown);
            Assert.DoesNotContain("mount-like", markdown, StringComparison.Ordinal);
        }
        finally
        {
            MarkdownListing.EnumerateFiles = originalEnumerate;
            MarkdownListing.InspectEntry = originalInspect;
        }
    }

    /// <summary>
    /// The root is not a nested directory: its failure must reach the caller. UI_SPEC §5 defines no
    /// access-denied line for the root, and the §7 CLI contract needs "could not list anything" to be
    /// distinguishable from "listed an unreadable subfolder" (DECISIONS.md D5). The depth-0 shortcut
    /// must not bypass that check.
    /// </summary>
    [Fact]
    public void NonexistentRoot_Throws()
    {
        using var tree = new TestTreeBuilder();
        string missing = Path.Combine(tree.Root, "does-not-exist");

        Assert.False(Directory.Exists(missing));
        Assert.ThrowsAny<IOException>(() =>
            MarkdownListing.Generate(missing, BaseOptions(), null, CancellationToken.None));

        // Depth 0 discards the children but must still prove the root is readable.
        ListingOptions depthZero = BaseOptions();
        depthZero.Depth = 0;
        Assert.ThrowsAny<IOException>(() =>
            MarkdownListing.Generate(missing, depthZero, null, CancellationToken.None));
    }

    /// <summary>
    /// One entry that cannot be inspected (it vanished between enumeration and inspection, or access
    /// was denied for it) is skipped; the listing continues and every other entry is still emitted.
    /// This exercises the per-entry recovery branch, not just the per-directory one.
    /// </summary>
    [Fact]
    public void UninspectableEntry_IsSkippedAndListingContinues()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/kept-a.txt");
        tree.AddFile("omega/kept-z.txt");
        string vanished = Path.Combine(tree.Root, "alpha", "vanished.txt");

        Func<string, string[]> originalEnumerate = MarkdownListing.EnumerateFiles;
        Func<string, MarkdownListing.EntryInfo?> originalInspect = MarkdownListing.InspectEntry;

        MarkdownListing.EnumerateFiles = path =>
            path.Equals(Path.Combine(tree.Root, "alpha"), StringComparison.OrdinalIgnoreCase)
                ? [.. originalEnumerate(path), vanished]
                : originalEnumerate(path);

        // The seam throws here, which is what the real implementation does for a vanished entry
        // (File.GetAttributes raises FileNotFoundException); the engine converts it to a skip.
        MarkdownListing.InspectEntry = path => path.Equals(vanished, StringComparison.OrdinalIgnoreCase)
            ? throw new FileNotFoundException($"simulated vanished entry {path}")
            : originalInspect(path);

        try
        {
            string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [alpha]\n" +
                "        - kept-a.txt\n" +
                "    - [omega]\n" +
                "        - kept-z.txt\n";
            Assert.Equal(expected, markdown);
            Assert.DoesNotContain("vanished", markdown, StringComparison.Ordinal);
        }
        finally
        {
            MarkdownListing.EnumerateFiles = originalEnumerate;
            MarkdownListing.InspectEntry = originalInspect;
        }
    }

    /// <summary>
    /// UI_SPEC §6 / M1-review carry-over: a junction pointing at an ancestor must not make the
    /// traversal recurse forever. This test is the regression net for the M1 reparse-point guard
    /// (KNOWN_LIMITATIONS L2) — it is load-bearing, not optional. The call runs on a worker with a
    /// bounded wait so a regression fails the test instead of hanging the whole run.
    /// </summary>
    [Fact]
    public async Task NotFollowed_CycleTerminates()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("real/a.txt");
        string link = CreateDirectoryLink(tree, "loop", tree.Root);

        Task<string> listing = Task.Run(() =>
            MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None));

        Task finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            ReferenceEquals(finished, listing),
            "the traversal did not terminate within 30s - the reparse-point guard has regressed");

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [loop] (link → " + linkTarget + ")\n" +
            "    - [real]\n" +
            "        - a.txt\n";
        Assert.Equal(expected, await listing);
    }

    /// <summary>
    /// Acceptance criterion 10: a 300+ level tree must not overflow the stack. The traversal is
    /// iterative (LEARN[2]), so depth is bounded by memory rather than by the call stack.
    /// </summary>
    [Fact]
    public void DeepTree300Levels_NoOverflow()
    {
        const int depth = 320;

        using var tree = new TestTreeBuilder();
        string relative = string.Join('/', Enumerable.Repeat("d", depth));
        tree.AddFile(relative + "/leaf.txt");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        string[] lines = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(depth + 2, lines.Length); // root + 320 folders + the leaf
        Assert.Equal("- [" + tree.RootName + "]", lines[0]);
        // The i-th folder is at level i, so the leaf (a child of the 320th folder) is at level 321.
        Assert.Equal(new string(' ', 4 * (depth + 1)) + "- leaf.txt", lines[^1]);
    }

    /// <summary>UI_SPEC §5 grammar: <c>indent = indentSize spaces × level</c>, for both allowed values.</summary>
    /// <param name="indentSize">Indent width under test (2 or 4, per §4).</param>
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void IndentSize2And4_Respected(int indentSize)
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");

        ListingOptions options = BaseOptions();
        options.IndentSize = indentSize;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            new string(' ', indentSize) + "- [alpha]\n" +
            new string(' ', indentSize * 2) + "- inner.txt\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 8 also fixes the encoding of a saved listing: UTF-8 without a BOM. This pins
    /// the engine's own output (the Save As dialog is M5 work).
    /// </summary>
    [Fact]
    public void Output_HasNoByteOrderMark()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        Assert.False(markdown.StartsWith('\uFEFF'), "listing must not start with a BOM");
        Assert.Equal(markdown, new UTF8Encoding(false).GetString(new UTF8Encoding(false).GetBytes(markdown)));
        Assert.Equal($"- [{tree.RootName}]", markdown.Split('\n')[0]);
    }

    /// <summary>
    /// UI_SPEC §5 rule 2: file sizes use <c>N B</c> / <c>N.N KB</c> / <c>N.N MB</c>, one decimal, with
    /// a trailing <c>.0</c> trimmed — the exact strings the rule names.
    /// </summary>
    [Fact]
    public void Rule2_SizeFormat_TrimsTrailingPointZero()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("a1023.bin", 1023);
        tree.AddFileOfSize("b1024.bin", 1024);
        tree.AddFileOfSize("c1536.bin", 1536);
        tree.AddFileOfSize("d2mb.bin", 2 * 1024 * 1024);

        ListingOptions options = BaseOptions();
        options.ShowFileSizes = true;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - a1023.bin (1023 B)\n" +
            "    - b1024.bin (1 KB)\n" +
            "    - c1536.bin (1.5 KB)\n" +
            "    - d2mb.bin (2 MB)\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>UI_SPEC §5 rule 2: the size comes before the attribute group on a file line.</summary>
    [Fact]
    public void Rule2_SizeBeforeAttributes()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("report.bin", 1024);

        var options = new ListingOptions { ShowFileSizes = true, ShowAttributes = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        Assert.Equal($"- [{tree.RootName}]\n    - report.bin (1 KB) [A]\n", markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 3: attribute flags appear in the fixed order H, R, S, A with only the present
    /// flags shown.
    /// </summary>
    [Fact]
    public void Rule3_AttributeOrderHRSA()
    {
        using var tree = new TestTreeBuilder();
        string all = tree.AddFile("all-flags.txt");
        File.SetAttributes(all, FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Archive);
        string some = tree.AddFile("some-flags.txt");
        File.SetAttributes(some, FileAttributes.ReadOnly | FileAttributes.Archive);

        var options = new ListingOptions { ShowAttributes = true, ShowFileSizes = false, IncludeHidden = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}]\n" +
            "    - all-flags.txt [HRSA]\n" +
            "    - some-flags.txt [RA]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 3: the bracket group is omitted entirely when no flag applies, and when the
    /// attribute setting is off.
    /// </summary>
    [Fact]
    public void Rule3_BracketsOmittedWhenNoFlags()
    {
        using var tree = new TestTreeBuilder();
        string plain = tree.AddFile("plain.txt");
        File.SetAttributes(plain, FileAttributes.Normal);

        var withAttributes = new ListingOptions { ShowAttributes = true, ShowFileSizes = false };
        string markdown = MarkdownListing.Generate(tree.Root, withAttributes, null, CancellationToken.None);
        Assert.Equal($"- [{tree.RootName}]\n    - plain.txt\n", markdown);

        var withoutAttributes = new ListingOptions { ShowAttributes = false, ShowFileSizes = false };
        markdown = MarkdownListing.Generate(tree.Root, withoutAttributes, null, CancellationToken.None);
        Assert.Equal($"- [{tree.RootName}]\n    - plain.txt\n", markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 7: a folder's size is the recursive sum of the file sizes inside it, and the
    /// hidden-files setting applies to the sum as well as to the listing.
    /// </summary>
    [Fact]
    public void Rule7_FolderSizeIsRecursiveSum_RespectsHidden()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("outer/visible.bin", 1000);
        tree.AddFileOfSize("outer/hidden.bin", 500);
        tree.MarkHidden("outer/hidden.bin");

        var withoutHidden = new ListingOptions { ShowFolderSizes = true, ShowFileSizes = false, ShowAttributes = false };
        string markdown = MarkdownListing.Generate(tree.Root, withoutHidden, null, CancellationToken.None);
        Assert.Equal(
            $"- [{tree.RootName}] (1000 B)\n    - [outer] (1000 B)\n        - visible.bin\n",
            markdown);

        var withHidden = new ListingOptions { ShowFolderSizes = true, ShowFileSizes = false, ShowAttributes = false, IncludeHidden = true };
        markdown = MarkdownListing.Generate(tree.Root, withHidden, null, CancellationToken.None);
        Assert.Equal(
            $"- [{tree.RootName}] (1.5 KB)\n    - [outer] (1.5 KB)\n        - hidden.bin\n        - visible.bin\n",
            markdown);
    }

    /// <summary>
    /// UI_SPEC §5 rule 7: when any access-denied subtree contributes to a sum, the folder suffix
    /// becomes <c>(~N.N MB)</c> — the tilde marks a partial sum.
    /// </summary>
    [Fact]
    public void Rule7_PartialSumGetsTilde()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("outer/visible.bin", 1000);
        string deniedPath = tree.AddDirectory("outer/denied");
        tree.AddFileOfSize("outer/denied/hidden-away.bin", 500);

        Func<string, string[]> original = MarkdownListing.EnumerateDirectories;
        MarkdownListing.EnumerateDirectories = path =>
            path.Equals(deniedPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"simulated denial for {path}")
                : original(path);

        try
        {
            var options = new ListingOptions { ShowFolderSizes = true, ShowAttributes = false };

            string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

            string expected =
                $"- [{tree.RootName}] (~1000 B)\n" +
                "    - [outer] (~1000 B)\n" +
                "        - [outer/denied] (access denied)\n" +
                "        - visible.bin (1000 B)\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateDirectories = original;
        }
    }

    /// <summary>
    /// M1-review Q6 / UI_SPEC §5 table: the root line takes a folder size but never attribute flags
    /// (rule 3 has no root variant), while a nested folder may have both.
    /// </summary>
    [Fact]
    public void RootFolder_HasSizeButNoAttributes()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("payload.bin", 1024);
        string folder = tree.AddDirectory("nested");
        File.SetAttributes(folder, FileAttributes.ReadOnly);
        tree.AddFileOfSize("nested/inner.bin", 1024);

        var options = new ListingOptions { ShowFolderSizes = true, ShowAttributes = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string expected =
            $"- [{tree.RootName}] (2 KB)\n" +
            "    - [nested] (1 KB) [R]\n" +
            "        - inner.bin (1 KB) [A]\n" +
            "    - payload.bin (1 KB) [A]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// M2-review carry-over 1b: with <c>followSymlinks</c> on, a link whose target has not been
    /// entered yet is emitted with the <c>→ followed</c> marker and the target's children are listed
    /// beneath it as normal (annotated) entries — the link line itself still carries only the link
    /// annotation.
    /// </summary>
    [Fact]
    public void FollowLink_EmitsFollowedSuffixAndChildren()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("zebra");
        tree.AddFileOfSize("zebra/inside.bin", 1024);
        string link = CreateDirectoryLink(tree, "alpha", target);

        var options = new ListingOptions { FollowSymlinks = true, ShowFileSizes = true, ShowFolderSizes = true, ShowAttributes = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            // Root sums its real content once: rule 7 forbids entering the junction while summing.
            $"- [{tree.RootName}] (1 KB)\n" +
            "    - [alpha] (link → " + linkTarget + " → followed)\n" +
            "        - inside.bin (1 KB) [A]\n" +
            "    - [zebra] (1 KB)\n" +
            "        - inside.bin (1 KB) [A]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// M3-review item 4 / D10: cycle detection is ancestry-based, so a link to a directory that merely
    /// appears elsewhere in the tree — a sibling listed *before* the link — is followed, and the §5
    /// "children as normal entries" rule means its content is listed twice (under the link and under
    /// the real path). Only a target on the current ancestor path is a cycle.
    /// </summary>
    [Fact]
    public void FollowLink_ToAlreadyListedSibling_IsFollowed()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("alpha");
        tree.AddFileOfSize("alpha/inside.bin", 1024);
        // "zulu" sorts after "alpha", so alpha is expanded (and the link is popped) later.
        string link = CreateDirectoryLink(tree, "zulu", target);

        var options = new ListingOptions { FollowSymlinks = true, ShowFileSizes = true, ShowFolderSizes = true, ShowAttributes = true };

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}] (1 KB)\n" +
            "    - [alpha] (1 KB)\n" +
            "        - inside.bin (1 KB) [A]\n" +
            "    - [zulu] (link → " + linkTarget + " → followed)\n" +
            "        - inside.bin (1 KB) [A]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// M3-review item 1 / UI_SPEC §5 rule 4: the depth limit applies to a followed link exactly as it
    /// does to a folder — the link's own line is emitted, its children are not. The bare link form is
    /// used because "→ followed" would claim children that are not there.
    /// </summary>
    [Fact]
    public void FollowLink_RespectsDepthLimit()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("zebra");
        tree.AddFile("zebra/inside.txt");
        string link = CreateDirectoryLink(tree, "alpha", target);

        ListingOptions options = BaseOptions();
        options.FollowSymlinks = true;
        options.Depth = 1;

        string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [alpha] (link → " + linkTarget + ")\n" +
            "    - [zebra]\n";
        Assert.Equal(expected, markdown);
    }

    /// <summary>
    /// M3-review item 2: with <c>ShowFolderSizes</c> and <c>FollowSymlinks</c> both on, a junction
    /// cycle must terminate quickly. UI_SPEC §5 rule 7 says reparse points are never entered for sums
    /// in either link mode, and the size walk is iterative (D9), so the cycle is never entered at all.
    /// </summary>
    [Fact]
    public async Task ShowFolderSizes_FollowSymlinks_CyclicJunction_Terminates()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFileOfSize("real/a.bin", 1024);
        string link = CreateDirectoryLink(tree, "loop", tree.Root);

        var options = new ListingOptions { ShowFolderSizes = true, FollowSymlinks = true, ShowFileSizes = true, ShowAttributes = false };

        Task<string> listing = Task.Run(() => MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None));

        Task finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(
            ReferenceEquals(finished, listing),
            "the size walk did not terminate within 10s - reparse points are being entered while summing");

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}] (1 KB)\n" +
            "    - [loop] (link → " + linkTarget + ")\n" +
            "    - [real] (1 KB)\n" +
            "        - a.bin (1 KB)\n";
        Assert.Equal(expected, await listing);
    }

    /// <summary>
    /// M3-review item 6(a): when the target's identity cannot be read, the link is emitted in the
    /// not-followed form and is not entered — the engine must never claim to follow something it
    /// could not check for cycles.
    /// </summary>
    [Fact]
    public void FollowLink_IdentityUnreadable_EmitsBareLinkLine()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("zebra");
        tree.AddFile("zebra/inside.txt");
        string link = CreateDirectoryLink(tree, "alpha", target);

        Func<string, FileIdentity?> original = MarkdownListing.ReadFileIdentity;
        MarkdownListing.ReadFileIdentity = path =>
            path.Equals(link, StringComparison.OrdinalIgnoreCase) ? null : original(path);

        try
        {
            ListingOptions options = BaseOptions();
            options.FollowSymlinks = true;

            string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

            string linkTarget = new DirectoryInfo(link).LinkTarget!;
            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [alpha] (link → " + linkTarget + ")\n" +
                "    - [zebra]\n" +
                "        - inside.txt\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.ReadFileIdentity = original;
        }
    }

    /// <summary>
    /// M3 round-2 review (D13): a failed identity read for an <b>intermediate</b> directory must not
    /// stop a deeper link back to it from terminating. Because that directory's own identity is the
    /// one that could not be read, the first encounter cannot be recognised by identity; the link is
    /// therefore followed once, which records its target's identity, and the next encounter is
    /// detected as a cycle. The guard being tested is termination plus that single extra level — not
    /// an unbounded traversal.
    /// </summary>
    [Fact]
    public async Task CycleWithUnreadableAncestorIdentity_Terminates()
    {
        using var tree = new TestTreeBuilder();
        string mid = tree.AddDirectory("mid");
        tree.AddDirectory("mid/deep");
        string loop = CreateDirectoryLink(tree, "mid/deep/loop", mid);

        Func<string, FileIdentity?> original = MarkdownListing.ReadFileIdentity;
        MarkdownListing.ReadFileIdentity = path =>
            path.Equals(mid, StringComparison.OrdinalIgnoreCase) ? null : original(path);

        try
        {
            ListingOptions options = BaseOptions();
            options.FollowSymlinks = true;

            Task<string> listing = Task.Run(() => MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None));
            Task finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(30)));
            Assert.True(
                ReferenceEquals(finished, listing),
                "a link back to an intermediate directory whose identity is unreadable did not terminate within 30s");

            string linkTarget = new DirectoryInfo(loop).LinkTarget!;
            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [mid]\n" +
                "        - [mid/deep]\n" +
                "            - [mid/deep/loop] (link → " + linkTarget + " → followed)\n" +
                "                - [mid/deep/loop/deep]\n" +
                "                    - [mid/deep/loop/deep/loop] (link → " + linkTarget + ")\n";
            Assert.Equal(expected, await listing);
        }
        finally
        {
            MarkdownListing.ReadFileIdentity = original;
        }
    }

    /// <summary>
    /// M3 round-2 review (D13) — the regression test for the ancestry-list invariant: position must
    /// always equal tree level, so a failed identity read leaves a <c>null</c> slot instead of shifting
    /// deeper entries down.
    /// </summary>
    /// <remarks>
    /// The fixture makes two ancestors of <c>p/q/r</c> unreadable while <c>p/s/link</c> targets
    /// <c>r</c>. Without the invariant, <c>r</c> is recorded one or two slots below its level and
    /// survives the truncation performed for its sibling <c>s</c>, so the link is misreported as a
    /// cycle and printed in the not-followed form. With the invariant, <c>r</c> is truncated away with
    /// the rest of the branch it belongs to and the link is followed, because <c>r</c> is not an
    /// ancestor of the link.
    /// </remarks>
    [Fact]
    public void FailedIdentityRead_KeepsAncestrySlotsAligned()
    {
        using var tree = new TestTreeBuilder();
        string p = tree.AddDirectory("p");
        string q = tree.AddDirectory("p/q");
        string r = tree.AddDirectory("p/q/r");
        tree.AddDirectory("p/s");
        string link = CreateDirectoryLink(tree, "p/s/link", r);

        Func<string, FileIdentity?> original = MarkdownListing.ReadFileIdentity;
        MarkdownListing.ReadFileIdentity = path =>
            path.Equals(p, StringComparison.OrdinalIgnoreCase) || path.Equals(q, StringComparison.OrdinalIgnoreCase)
                ? null
                : original(path);

        try
        {
            ListingOptions options = BaseOptions();
            options.FollowSymlinks = true;

            string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

            string linkTarget = new DirectoryInfo(link).LinkTarget!;
            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [p]\n" +
                "        - [p/q]\n" +
                "            - [p/q/r]\n" +
                "        - [p/s]\n" +
                "            - [p/s/link] (link → " + linkTarget + " → followed)\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.ReadFileIdentity = original;
        }
    }

    /// <summary>
    /// M3-review item 6(b): a followed link whose target cannot be enumerated keeps its
    /// <c>→ followed</c> line and simply has no children — one bad directory never aborts the run.
    /// </summary>
    [Fact]
    public void FollowLink_TargetUnreadable_EmitsFollowedLineWithoutChildren()
    {
        using var tree = new TestTreeBuilder();
        string target = tree.AddDirectory("zebra");
        tree.AddFile("zebra/inside.txt");
        string link = CreateDirectoryLink(tree, "alpha", target);

        Func<string, string[]> original = MarkdownListing.EnumerateDirectories;
        MarkdownListing.EnumerateDirectories = path =>
            path.Equals(link, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"simulated denial for {path}")
                : original(path);

        try
        {
            ListingOptions options = BaseOptions();
            options.FollowSymlinks = true;

            string markdown = MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None);

            string linkTarget = new DirectoryInfo(link).LinkTarget!;
            string expected =
                $"- [{tree.RootName}]\n" +
                "    - [alpha] (link → " + linkTarget + " → followed)\n" +
                "    - [zebra]\n" +
                "        - inside.txt\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateDirectories = original;
        }
    }

    /// <summary>
    /// UI_SPEC §6: with <c>followSymlinks</c> on, an already-entered target is emitted as a link line
    /// without recursion, so a junction cycle terminates in followed mode too.
    /// </summary>
    [Fact]
    public async Task FollowLink_CycleTerminates()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("real/a.txt");
        string link = CreateDirectoryLink(tree, "loop", tree.Root);

        var options = new ListingOptions { FollowSymlinks = true, ShowFileSizes = false, ShowAttributes = false };

        Task<string> listing = Task.Run(() => MarkdownListing.Generate(tree.Root, options, null, CancellationToken.None));

        Task finished = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.True(
            ReferenceEquals(finished, listing),
            "followed mode did not terminate within 30s - the file-identity cycle check has regressed");

        string linkTarget = new DirectoryInfo(link).LinkTarget!;
        string expected =
            $"- [{tree.RootName}]\n" +
            "    - [loop] (link → " + linkTarget + ")\n" + // already-visited target: no "→ followed"
            "    - [real]\n" +
            "        - a.txt\n";
        Assert.Equal(expected, await listing);
    }

    /// <summary>
    /// UI_SPEC §6: <c>IProgress&lt;int&gt;</c> reports the number of entries enumerated, and the
    /// last report matches the number of entries in the listing.
    /// </summary>
    [Fact]
    public void ProgressReportsEntries()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");
        tree.AddFile("beta/inner.txt");
        tree.AddDirectory("empty");

        var progress = new CollectingProgress();

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), progress, CancellationToken.None);

        int entries = markdown.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1; // minus root
        Assert.Equal(5, entries); // alpha, inner.txt, beta, inner.txt, empty
        Assert.NotEmpty(progress.Values);
        Assert.Equal(entries, progress.Values[^1]);
        Assert.Equal(Enumerable.Range(1, entries), progress.Values);
    }

    /// <summary>UI_SPEC §6: cancellation is checked between directories and propagates to the caller.</summary>
    [Fact]
    public void Cancellation_ThrowsOperationCanceled()
    {
        using var tree = new TestTreeBuilder();
        tree.AddFile("alpha/inner.txt");
        tree.AddFile("beta/inner.txt");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            MarkdownListing.Generate(tree.Root, BaseOptions(), null, cts.Token));
    }

    /// <summary>
    /// Acceptance criterion 11 / UI_SPEC §6: a path longer than MAX_PATH is enumerated as-is. The test
    /// is conditional — it needs the machine-wide <c>LongPathsEnabled</c> setting, which M4's elevated
    /// button exists to turn on.
    /// </summary>
    [Fact]
    public void LongPath_Over260Chars_Listed()
    {
        if (!LongPathsAreEnabled())
        {
            // xunit 2.5.3 has no dynamic skip, so an unmet precondition returns with a visible note
            // instead of a silent pass; the note is printed into the test output.
            Console.WriteLine(
                "SKIPPED LongPath_Over260Chars_Listed: HKLM\\SYSTEM\\CurrentControlSet\\Control\\FileSystem\\LongPathsEnabled is not 1.");
            return;
        }

        using var tree = new TestTreeBuilder();
        string segment = new string('p', 40);
        string nested = string.Join('/', Enumerable.Repeat(segment, 6)); // > 260 characters in total
        string deepFile = tree.AddFile(nested + "/deep.txt");

        Assert.True(deepFile.Length > 260, $"fixture path is only {deepFile.Length} characters");

        string markdown = MarkdownListing.Generate(tree.Root, BaseOptions(), null, CancellationToken.None);

        Assert.Contains(string.Join('/', Enumerable.Repeat(segment, 6)), markdown);
        Assert.Contains("deep.txt", markdown);
    }

    /// <summary>
    /// M2-review carry-over 4 / PLAN.md M3 DoD / M3-review item 7: the UI_SPEC §5 reference example,
    /// asserted byte-for-byte against the spec text over a fixture built to match it exactly.
    /// </summary>
    /// <remarks>
    /// The fixture is the one the review asked for: root <c>App</c>, <c>docs/spec/format-v1.md</c>
    /// 4813 B, <c>docs/README.md</c> 1126 B, <c>src/main.py</c> 2355 B, <c>protected</c> access-denied
    /// through the seam, and <c>shared</c> a real junction that is not followed (default options).
    /// The expectation is the spec's example text verbatim.
    /// <para>
    /// <b>Skipped, not adapted.</b> The spec was corrected in the M3 review round (folder paths are
    /// now root-relative, `[App/docs]` became `[docs]`, the numbers were fixed — D11), so the only
    /// remaining difference is the link-target line: the example shows the illustrative
    /// <c>(link → ..\shared-lib)</c>, while rule 11 says the target is emitted as stored and a junction
    /// created by <c>mklink /J</c> stores an absolute path. Producing the example's line needs a link
    /// with a *relative* stored target, which requires Developer Mode or elevation (KNOWN_LIMITATIONS
    /// L4b). Everything else in the example is asserted by
    /// <see cref="ReferenceExample_EverythingButTheLinkTarget"/>.
    /// </para>
    /// </remarks>
    [Fact(Skip = "Blocked on the illustrative link-target line only (KNOWN_LIMITATIONS L6 + L4b): the §5 example shows " +
                 "'(link → ..\\shared-lib)' while rule 11 says the target is emitted as stored and a junction stores an " +
                 "absolute path; a relative-target link needs Developer Mode/elevation. Re-enable on such a machine; " +
                 "the assertion below is the literal spec text.")]
    public void ReferenceExample_ByteForByte()
    {
        using var tree = new TestTreeBuilder();
        string app = tree.AddDirectory("App");
        string sharedLib = tree.AddDirectory("shared-lib");
        tree.AddFileOfSize("shared-lib/lib.bin", 512);
        tree.AddFileOfSize("App/docs/spec/format-v1.md", 4813);
        tree.AddFileOfSize("App/docs/README.md", 1126);
        string protectedPath = tree.AddDirectory("App/protected");
        tree.AddFileOfSize("App/protected/secret.bin", 4096);
        CreateDirectoryLink(tree, "App/shared", sharedLib);
        tree.AddFileOfSize("App/src/main.py", 2355);

        Func<string, string[]> original = MarkdownListing.EnumerateDirectories;
        MarkdownListing.EnumerateDirectories = path =>
            path.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"simulated denial for {path}")
                : original(path);

        try
        {
            var options = new ListingOptions
            {
                ShowFileSizes = true,
                ShowFolderSizes = true,
                ShowAttributes = true,
                IndentSize = 4,
                Depth = -1,
            };

            string markdown = MarkdownListing.Generate(app, options, null, CancellationToken.None);

            const string specExample =
                "- [App] (~8.1 KB)\n" +
                "    - [docs] (5.8 KB)\n" +
                "        - [docs/spec] (4.7 KB)\n" +
                "            - format-v1.md (4.7 KB) [A]\n" +
                "        - README.md (1.1 KB) [A]\n" +
                "    - [protected] (access denied)\n" +
                "    - [shared] (link → ..\\shared-lib)\n" +
                "    - [src] (2.3 KB)\n" +
                "        - main.py (2.3 KB) [A]\n";

            Assert.Equal(specExample, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateDirectories = original;
        }
    }

    /// <summary>
    /// The reference-example fixture asserted byte-for-byte over everything except the link-target
    /// line: the corrected §5 example's numbers (<c>~8.1 KB</c>, <c>5.8 KB</c>, <c>4.7 KB</c>,
    /// <c>1.1 KB</c>, <c>2.3 KB</c>), its root-relative folder paths, the suffix order, the
    /// <c>[A]</c> flags, the access-denied line, the link annotation, folder-first ordering, 4-space
    /// indentation and the single trailing newline.
    /// </summary>
    /// <remarks>
    /// The only line expressed differently is the shared-link target: the engine prints the target
    /// **as stored** (rule 11), which for a <c>mklink /J</c> junction is an absolute path, while the
    /// example shows the illustrative <c>..\shared-lib</c>. That single gap is KNOWN_LIMITATIONS L6 and
    /// is pinned by <see cref="ReferenceExample_ByteForByte"/>, which holds the literal text and is
    /// skipped until a Developer Mode machine can create a relative-target link (L4b).
    /// </remarks>
    [Fact]
    public void ReferenceExample_EverythingButTheLinkTarget()
    {
        using var tree = new TestTreeBuilder();
        string app = tree.AddDirectory("App");
        string sharedLib = tree.AddDirectory("shared-lib");
        tree.AddFileOfSize("shared-lib/lib.bin", 512);
        tree.AddFileOfSize("App/docs/spec/format-v1.md", 4813);
        tree.AddFileOfSize("App/docs/README.md", 1126);
        string protectedPath = tree.AddDirectory("App/protected");
        tree.AddFileOfSize("App/protected/secret.bin", 4096);
        string shared = CreateDirectoryLink(tree, "App/shared", sharedLib);
        tree.AddFileOfSize("App/src/main.py", 2355);

        Func<string, string[]> original = MarkdownListing.EnumerateDirectories;
        MarkdownListing.EnumerateDirectories = path =>
            path.Equals(protectedPath, StringComparison.OrdinalIgnoreCase)
                ? throw new UnauthorizedAccessException($"simulated denial for {path}")
                : original(path);

        try
        {
            var options = new ListingOptions
            {
                ShowFileSizes = true,
                ShowFolderSizes = true,
                ShowAttributes = true,
                IndentSize = 4,
                Depth = -1,
            };

            string markdown = MarkdownListing.Generate(app, options, null, CancellationToken.None);

            string linkTarget = new DirectoryInfo(shared).LinkTarget!;
            string expected =
                "- [App] (~8.1 KB)\n" +
                "    - [docs] (5.8 KB)\n" +
                "        - [docs/spec] (4.7 KB)\n" +
                "            - format-v1.md (4.7 KB) [A]\n" +
                "        - README.md (1.1 KB) [A]\n" +
                "    - [protected] (access denied)\n" +
                "    - [shared] (link → " + linkTarget + ")\n" +
                "    - [src] (2.3 KB)\n" +
                "        - main.py (2.3 KB) [A]\n";
            Assert.Equal(expected, markdown);
        }
        finally
        {
            MarkdownListing.EnumerateDirectories = original;
        }
    }

    /// <summary>
    /// The fixture builder's teardown must cope with the shapes these tests create: a junction nested
    /// below the root (the old teardown unlinked only the root's immediate links, so the recursive delete
    /// walked through it and the whole fixture survived every run), a read-only file, a hidden read-only
    /// file, and — since M7 item 2 — **read-only junction entries**, which cannot be unlinked until their
    /// own flag is cleared. It must also never delete *through* a link: the junction's target is a tree of
    /// its own and has to survive.
    /// </summary>
    /// <remarks>
    /// This lives here rather than in a test class of its own so it can reuse this file's link helper,
    /// which already knows the Developer-Mode / <c>mklink /J</c> fallback. Added for OCR run-4 finding 3:
    /// without it the teardown is only proven by the suite's own fixture count, and <c>Dispose</c>
    /// swallows its failures. The outer <c>finally</c> removes the links this test made (never through
    /// them) and then the two roots, so a regression fails the test **and** leaves the temp directory as
    /// clean as it found it (M7 item 3, from OCR run-5 finding 4).
    /// </remarks>
    [Fact]
    public void FixtureTeardown_RemovesNestedLinksAndReadOnlyFiles_AndKeepsTheLinkTarget()
    {
        string target = Path.Combine(Path.GetTempPath(), "FolderTreeMD.Tests", "target-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "keep");

        string root = string.Empty;
        string loopLink = string.Empty;
        string outsideLink = string.Empty;

        try
        {
            var tree = new TestTreeBuilder();
            try
            {
                root = tree.Root;
                tree.AddDirectory("mid/deep");
                tree.AddFile("mid/readonly.txt");
                tree.AddFile("hidden-readonly.txt");

                File.SetAttributes(tree.FullPath("mid/readonly.txt"), FileAttributes.ReadOnly);
                File.SetAttributes(
                    tree.FullPath("hidden-readonly.txt"),
                    FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);

                // A junction back at an ancestor (the cycle case) and one out of the tree entirely. Both
                // are left read-only: a link carries attributes of its own, and a read-only link cannot be
                // unlinked until the teardown clears that flag (measured for M7 item 2).
                loopLink = CreateDirectoryLink(tree, "mid/deep/loop", tree.FullPath("mid"));
                outsideLink = CreateDirectoryLink(tree, "outside", target);

                File.SetAttributes(loopLink, File.GetAttributes(loopLink) | FileAttributes.ReadOnly);
                File.SetAttributes(outsideLink, File.GetAttributes(outsideLink) | FileAttributes.ReadOnly);

                // The fixture only pins the teardown's read-only clears if the entries really carry the
                // flag: on a filesystem that ignores it, the pre-pass could skip every clear and the
                // fixture would still be deleted happily, so the coverage would vanish silently
                // (OCR run-6 finding 2, extended to the files in the M7 fix round). Failing loudly here is
                // the point — a platform that cannot set the flag cannot verify the behaviour either.
                foreach (string flagged in new[]
                {
                    tree.FullPath("mid/readonly.txt"),
                    tree.FullPath("hidden-readonly.txt"),
                    loopLink,
                    outsideLink,
                })
                {
                    Assert.True(
                        (File.GetAttributes(flagged) & FileAttributes.ReadOnly) != 0,
                        $"'{flagged}' did not accept the ReadOnly flag, so this test cannot pin the read-only clear");
                }
            }
            finally
            {
                tree.Dispose();
            }

            Assert.False(Directory.Exists(root), "Dispose must remove the whole fixture tree, junctions included");
            Assert.True(Directory.Exists(target), "Dispose must not delete through a junction into another tree");
            Assert.True(File.Exists(Path.Combine(target, "keep.txt")), "the junction target's content must survive");
        }
        finally
        {
            // Best effort, and link-first on purpose: deleting a tree that still holds a junction would
            // walk into the target, so the links this test created go first, by name. The read-only files
            // it created are cleared by name too, because the teardown failure this test exists to catch
            // is exactly "the pre-pass aborted with read-only entries still in the tree", and a plain
            // recursive delete cannot remove those (measured: the first version of this cleanup left the
            // fixture behind for that reason).
            RemoveFixtureLink(loopLink);
            RemoveFixtureLink(outsideLink);

            // `root` stays empty when the fixture builder itself failed to be constructed, and
            // Path.Combine("", "mid", …) would then be a *relative* path resolved against the process
            // working directory (OCR run-7 finding 1). Guard it before combining.
            if (!string.IsNullOrEmpty(root))
            {
                ClearReadOnly(Path.Combine(root, "mid", "readonly.txt"));
                ClearReadOnly(Path.Combine(root, "hidden-readonly.txt"));
            }

            TryDeleteDirectory(root);
            TryDeleteDirectory(target);
        }
    }

    /// <summary>
    /// Removes one directory link a test created, clearing its read-only flag first. Does nothing when the
    /// link is already gone; never descends through it.
    /// </summary>
    /// <param name="linkPath">Absolute path of the link, or an empty string when it was never created.</param>
    private static void RemoveFixtureLink(string linkPath)
    {
        if (string.IsNullOrEmpty(linkPath))
        {
            return;
        }

        try
        {
            FileAttributes attributes = File.GetAttributes(linkPath);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return;
            }

            File.SetAttributes(linkPath, attributes & ~FileAttributes.ReadOnly);
            Directory.Delete(linkPath);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Clears one entry's read-only flag — file or directory — if it is still there and still flagged.
    /// An empty path is a no-op, so a fixture that was never created cannot make this resolve a relative
    /// path against the process working directory (OCR run-6 finding 1).
    /// </summary>
    /// <param name="path">File or directory to adjust, or an empty string when it was never created.</param>
    private static void ClearReadOnly(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return;
            }

            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes a directory tree if it is still there. Teardown must not fail an assertion.</summary>
    /// <param name="path">Directory to delete, or an empty string when it was never created.</param>
    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Creates a directory link (junction or symlink) from <paramref name="relativeLinkPath"/> to
    /// <paramref name="targetPath"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="Directory.CreateSymbolicLink"/> is tried first because it needs no child process;
    /// it requires Developer Mode or elevation on Windows. The documented fallback is <c>mklink /J</c>,
    /// which creates a junction and needs neither. If both fail the test fails with this reason
    /// instead of being skipped, because a silently skipped link test would remove the M1 guard's
    /// regression net (see the M1 review carry-over items).
    /// </remarks>
    /// <param name="tree">Fixture the link belongs to.</param>
    /// <param name="relativeLinkPath">Link path relative to the fixture root.</param>
    /// <param name="targetPath">Absolute path the link points at.</param>
    /// <returns>The absolute path of the created link.</returns>
    private static string CreateDirectoryLink(TestTreeBuilder tree, string relativeLinkPath, string targetPath)
    {
        string linkPath = tree.FullPath(relativeLinkPath);
        var symbolicLinkError = new List<string>();

        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return linkPath;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            symbolicLinkError.Add(exception.Message);
        }

        bool created = TryRunMklink("/J", linkPath, targetPath, out string output, out int exitCode);
        if (created && Directory.Exists(linkPath))
        {
            return linkPath;
        }

        throw new InvalidOperationException(
            "Could not create a directory link for this test, so the not-followed contract cannot be " +
            $"verified. Directory.CreateSymbolicLink failed: {string.Join("; ", symbolicLinkError)}. " +
            $"mklink /J exited {exitCode}: {output}");
    }

    /// <summary>
    /// Attempts to create a file symbolic link, returning <c>false</c> when the machine refuses
    /// (Windows needs Developer Mode or elevation for file symlinks).
    /// </summary>
    /// <param name="linkPath">Absolute path of the link to create.</param>
    /// <param name="targetPath">Absolute path of the target file.</param>
    /// <returns><c>true</c> when a real file symlink now exists at <paramref name="linkPath"/>.</returns>
    private static bool TryCreateFileSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return File.Exists(linkPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // mklink (without /J) needs the same privilege, so there is no fallback worth trying.
            return false;
        }
    }

    /// <summary>Reads the machine-wide long-path setting the engine's manifest cooperates with.</summary>
    /// <returns><c>true</c> when <c>LongPathsEnabled</c> is 1.</returns>
    private static bool LongPathsAreEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        object? value = Microsoft.Win32.Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\FileSystem",
            "LongPathsEnabled",
            null);
        return value is int enabled && enabled == 1;
    }

    /// <summary>
    /// Runs <c>mklink</c> with a bounded wait, draining stdout and stderr concurrently.
    /// </summary>
    /// <remarks>
    /// Blocking on one pipe and then the other is the classic redirected-pipe deadlock, and an
    /// unbounded <see cref="Process.WaitForExit()"/> would hang the test run if the child wedged.
    /// Output is captured with <c>BeginOutputReadLine</c> so both streams are consumed while the
    /// process runs, and the wait is capped at 20 seconds.
    /// </remarks>
    /// <param name="mode">Mode switch, e.g. <c>/J</c>.</param>
    /// <param name="linkPath">Path of the link.</param>
    /// <param name="targetPath">Path of the target.</param>
    /// <param name="output">Captured stdout and stderr, or the timeout/termination note.</param>
    /// <param name="exitCode">Child exit code, or <c>-1</c> when it had to be killed.</param>
    /// <returns><c>true</c> when the child exited 0 within the time limit.</returns>
    private static bool TryRunMklink(string mode, string linkPath, string targetPath, out string output, out int exitCode)
    {
        var startInfo = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add(mode);
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        var captured = new StringBuilder();
        using Process process = Process.Start(startInfo)!;
        process.OutputDataReceived += (_, e) => Append(captured, e.Data);
        process.ErrorDataReceived += (_, e) => Append(captured, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
        {
            process.Kill(entireProcessTree: true);
            output = $"timed out after 20s; captured: {captured.ToString().Trim()}";
            exitCode = -1;
            return false;
        }

        process.WaitForExit(); // flushes the async readers
        output = captured.ToString().Trim();
        exitCode = process.ExitCode;
        return exitCode == 0;
    }

    /// <summary>Appends one captured output line, ignoring the null lines the readers also raise.</summary>
    /// <param name="builder">Capture buffer.</param>
    /// <param name="line">Line to append, or <c>null</c> at end of stream.</param>
    private static void Append(StringBuilder builder, string? line)
    {
        if (line is not null)
        {
            builder.AppendLine(line);
        }
    }

    /// <summary>Collects <see cref="IProgress{T}"/> reports synchronously, so assertions can read them.</summary>
    private sealed class CollectingProgress : IProgress<int>
    {
        /// <summary>Values reported so far, in order.</summary>
        public List<int> Values { get; } = [];

        /// <summary>Records one report.</summary>
        /// <param name="value">Reported entry count.</param>
        public void Report(int value) => Values.Add(value);
    }
}
